// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.Invoker;

using System.Collections.Concurrent;

/// <summary>
/// Provides methods to invoke actions and functions asynchronously or synchronously on a specific thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading model.</b> An <see cref="Invoker"/> belongs to the thread that constructed it (the
/// "owner thread"). Work submitted from another thread is queued and runs only when
/// <see cref="DoInvokes"/> is pumped on the owner thread — typically once per iteration of a UI or
/// render loop. <see cref="Invoke(Action)"/> and <see cref="InvokeAsync(Action)"/> block/await the
/// owner thread, so they marshal work <i>onto</i> the owner thread; they do not make arbitrary code
/// thread-safe.
/// </para>
/// <para>
/// <b>Not for real-time audio threads.</b> The blocking and async paths allocate (a
/// <see cref="Task"/> per call) and can block the caller until the owner thread pumps, so they must
/// never be called from a hard real-time thread such as an audio callback. For the fire-and-forget,
/// non-blocking case from a non-real-time producer, prefer <see cref="TryBeginInvoke(Action)"/>,
/// which is allocation-free and bounded. Audio→UI telemetry should not go through the invoker at all;
/// publish it through a dedicated single-producer/single-consumer ring buffer instead.
/// </para>
/// </remarks>
/// <param name="beginInvokeCapacity">The capacity of the non-blocking <see cref="TryBeginInvoke(Action)"/> queue. Rounded up to the next power of two. At most 2^30 (1,073,741,824).</param>
/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="beginInvokeCapacity"/> is less than one or greater than 2^30.</exception>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Shutdown disposes the shutdown source. Making Invoker disposable as well would oblige every consumer to dispose it, where only those that shut it down hold anything to release.")]
public class Invoker(int beginInvokeCapacity)
{
	/// <summary>
	/// The default capacity of the non-blocking <see cref="TryBeginInvoke(Action)"/> queue.
	/// </summary>
	private const int DefaultBeginInvokeCapacity = 1024;

	private const TaskCreationOptions QueuedTaskOptions =
		TaskCreationOptions.RunContinuationsAsynchronously | TaskCreationOptions.DenyChildAttach;

	/// <summary>
	/// Gets the thread on which this instance was created.
	/// </summary>
	/// <remarks>
	/// The owner is held and compared by reference rather than by <see cref="Environment.CurrentManagedThreadId"/>:
	/// .NET reissues a managed thread ID once a dead thread's <see cref="Thread"/> object has been
	/// collected, so after the owner exits an unrelated thread could otherwise pass the owner check.
	/// </remarks>
	private Thread OwnerThread { get; } = Thread.CurrentThread;

	/// <summary>
	/// Gets a value indicating whether the calling thread is the one that created this instance.
	/// </summary>
	private bool IsOwnerThread => ReferenceEquals(Thread.CurrentThread, OwnerThread);

	/// <summary>
	/// The last submission number handed out. Work from both queues carries one, so
	/// <see cref="DoInvokes"/> can run it in the order it was submitted rather than queue by queue.
	/// </summary>
	private long submissionSequence;

	/// <summary>
	/// Whether <see cref="DoInvokes"/> is running on the owner thread. Read and written only there.
	/// </summary>
	private bool isPumping;

	/// <summary>
	/// The <see cref="TryBeginInvoke(Action)"/> failures the running pump will report once its work has
	/// run, or <see langword="null"/> while it has none. Read and written only on the owner thread.
	/// </summary>
	private List<Exception>? pumpFailures;

	/// <summary>
	/// Cancelled by <see cref="Shutdown"/>. Every queued <see cref="Invoke(Action)"/> and
	/// <see cref="InvokeAsync(Action)"/> task is created with this token, or with one linked to it, so
	/// shutting down cancels them all without the owner having to find each one.
	/// </summary>
	/// <remarks>
	/// <see cref="Shutdown"/> cancels and then disposes it. A disposed source still answers
	/// <see cref="CancellationTokenSource.IsCancellationRequested"/>, but its
	/// <see cref="CancellationTokenSource.Token"/> property throws, so the token is read only while
	/// <see cref="IsShutDown"/> is false.
	/// </remarks>
	private readonly CancellationTokenSource shutdownSource = new();

	/// <summary>
	/// Gets a value indicating whether <see cref="Shutdown"/> has been called.
	/// </summary>
	public bool IsShutDown => shutdownSource.IsCancellationRequested;

	/// <summary>
	/// Gets the queue of tasks to be executed, each with its submission number.
	/// </summary>
	internal ConcurrentQueue<(long Sequence, Task Task)> TaskQueue { get; } = new();

	/// <summary>
	/// Gets the bounded, lock-free queue backing <see cref="TryBeginInvoke(Action)"/>, each action with its submission number.
	/// </summary>
	private BoundedMpscQueue<(long Sequence, Action Action)> BeginInvokeQueue { get; } = new(beginInvokeCapacity);

	/// <summary>
	/// Initializes a new instance of the <see cref="Invoker"/> class owned by the calling thread, with
	/// the default non-blocking queue capacity.
	/// </summary>
	public Invoker()
		: this(DefaultBeginInvokeCapacity)
	{
	}

	/// <summary>
	/// Invokes the specified action asynchronously.
	/// </summary>
	/// <param name="func">The action to invoke.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the action is null.</exception>
	// Validation runs before the async core so a null argument throws at the call site, not into the task.
	public Task InvokeAsync(Action func)
	{
		Ensure.NotNull(func);
		return InvokeCoreAsync(func, CancellationToken.None);
	}

	/// <summary>
	/// Invokes the specified action asynchronously, unless it is cancelled before the owner thread runs it.
	/// </summary>
	/// <param name="func">The action to invoke.</param>
	/// <param name="cancellationToken">
	/// A token that withdraws the action while it is still queued. Once the owner thread has started
	/// the action, cancelling the token has no effect on it.
	/// </param>
	/// <returns>
	/// A task that represents the asynchronous operation. It is cancelled, and the action never runs,
	/// when <paramref name="cancellationToken"/> is cancelled or <see cref="Shutdown"/> is called before
	/// <see cref="DoInvokes"/> reaches the action.
	/// </returns>
	/// <exception cref="ArgumentNullException">Thrown when the action is null.</exception>
	public Task InvokeAsync(Action func, CancellationToken cancellationToken)
	{
		Ensure.NotNull(func);
		return InvokeCoreAsync(func, cancellationToken);
	}

	private async Task InvokeCoreAsync(Action func, CancellationToken cancellationToken)
	{
		if (IsOwnerThread)
		{
			cancellationToken.ThrowIfCancellationRequested();
			func();
			return;
		}

		// RunContinuationsAsynchronously keeps the caller's code after the await off the owner thread:
		// without it, DoInvokes' RunSynchronously would run that code inline, inside the pump.
		// DenyChildAttach stops an AttachedToParent child the delegate starts from holding up or faulting
		// this task, which the owner-thread fast path, with no task to attach to, never does.
		using CancellationTokenSource? linked = LinkToShutdown(cancellationToken, out CancellationToken queueToken);
		Task task = new(func, queueToken, QueuedTaskOptions);
		Enqueue(task);
		try
		{
			await task.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (task.IsCanceled)
		{
			throw WithdrawnException(cancellationToken);
		}
	}

	/// <summary>
	/// Invokes the specified action synchronously.
	/// </summary>
	/// <param name="func">The action to invoke.</param>
	/// <exception cref="ArgumentNullException">Thrown when the action is null.</exception>
	// GetAwaiter().GetResult() rethrows the delegate's own exception with its stack trace intact,
	// where unwrapping the AggregateException from Wait() and rethrowing it would reset the trace.
	public void Invoke(Action func) => InvokeAsync(func, CancellationToken.None).GetAwaiter().GetResult();

	/// <summary>
	/// Invokes the specified action synchronously, unless it is cancelled before the owner thread runs it.
	/// </summary>
	/// <param name="func">The action to invoke.</param>
	/// <param name="cancellationToken">
	/// A token that withdraws the action and releases the caller while the action is still queued.
	/// Once the owner thread has started the action, the caller waits for it to finish.
	/// </param>
	/// <exception cref="ArgumentNullException">Thrown when the action is null.</exception>
	/// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled, or <see cref="Shutdown"/> is called, before the owner thread runs the action. The action then never runs.</exception>
	// See Invoke(Action): GetResult() keeps the stack trace of the code that actually failed.
	public void Invoke(Action func, CancellationToken cancellationToken) => InvokeAsync(func, cancellationToken).GetAwaiter().GetResult();

	/// <summary>
	/// Invokes the specified function asynchronously and returns the result.
	/// </summary>
	/// <typeparam name="TReturn">The type of the return value.</typeparam>
	/// <param name="func">The function to invoke.</param>
	/// <returns>A task that represents the asynchronous operation, containing the result of the function.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	public Task<TReturn> InvokeAsync<TReturn>(Func<TReturn> func)
	{
		Ensure.NotNull(func);
		return InvokeCoreAsync(func, CancellationToken.None);
	}

	/// <summary>
	/// Invokes the specified function asynchronously and returns the result, unless it is cancelled
	/// before the owner thread runs it.
	/// </summary>
	/// <typeparam name="TReturn">The type of the return value.</typeparam>
	/// <param name="func">The function to invoke.</param>
	/// <param name="cancellationToken">See <see cref="InvokeAsync(Action, CancellationToken)"/>.</param>
	/// <returns>A task containing the result of the function, or cancelled as <see cref="InvokeAsync(Action, CancellationToken)"/> describes.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	public Task<TReturn> InvokeAsync<TReturn>(Func<TReturn> func, CancellationToken cancellationToken)
	{
		Ensure.NotNull(func);
		return InvokeCoreAsync(func, cancellationToken);
	}

	private async Task<TReturn> InvokeCoreAsync<TReturn>(Func<TReturn> func, CancellationToken cancellationToken)
	{
		if (IsOwnerThread)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return func();
		}

		// See InvokeAsync(Action): keep the caller's continuation out of DoInvokes.
		using CancellationTokenSource? linked = LinkToShutdown(cancellationToken, out CancellationToken queueToken);
		Task<TReturn> task = new(func, queueToken, QueuedTaskOptions);
		Enqueue(task);
		try
		{
			return await task.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (task.IsCanceled)
		{
			throw WithdrawnException(cancellationToken);
		}
	}

	/// <summary>
	/// Invokes the specified function synchronously and returns the result.
	/// </summary>
	/// <typeparam name="TReturn">The type of the return value.</typeparam>
	/// <param name="func">The function to invoke.</param>
	/// <returns>The result of the function.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	// See Invoke(Action): GetResult() keeps the stack trace of the code that actually failed.
	public TReturn Invoke<TReturn>(Func<TReturn> func) => InvokeAsync(func, CancellationToken.None).GetAwaiter().GetResult();

	/// <summary>
	/// Invokes the specified function synchronously and returns the result, unless it is cancelled
	/// before the owner thread runs it.
	/// </summary>
	/// <typeparam name="TReturn">The type of the return value.</typeparam>
	/// <param name="func">The function to invoke.</param>
	/// <param name="cancellationToken">See <see cref="Invoke(Action, CancellationToken)"/>.</param>
	/// <returns>The result of the function.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	/// <exception cref="OperationCanceledException">Thrown as <see cref="Invoke(Action, CancellationToken)"/> describes.</exception>
	// See Invoke(Action): GetResult() keeps the stack trace of the code that actually failed.
	public TReturn Invoke<TReturn>(Func<TReturn> func, CancellationToken cancellationToken) => InvokeAsync(func, cancellationToken).GetAwaiter().GetResult();

	/// <summary>
	/// Invokes the specified asynchronous function and waits for the task it returns to complete.
	/// </summary>
	/// <param name="func">The asynchronous function to invoke.</param>
	/// <returns>A task that completes when the task returned by <paramref name="func"/> completes.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	/// <remarks>
	/// The function starts on the owner thread. Without this overload an async lambda would bind to
	/// <see cref="InvokeAsync{TReturn}(Func{TReturn})"/>, whose task completes at the lambda's first
	/// incomplete await and never surfaces an exception thrown after it.
	/// </remarks>
	public Task InvokeAsync(Func<Task> func)
	{
		Ensure.NotNull(func);
		return InvokeAndAwaitCoreAsync(func, CancellationToken.None);
	}

	/// <summary>
	/// Invokes the specified asynchronous function and waits for the task it returns to complete,
	/// unless it is cancelled before the owner thread starts it.
	/// </summary>
	/// <param name="func">The asynchronous function to invoke.</param>
	/// <param name="cancellationToken">
	/// A token that withdraws the function while it is still queued. Once the owner thread has started
	/// the function, cancelling the token has no effect on it; pass the token into the function to stop
	/// its own work.
	/// </param>
	/// <returns>A task that completes when the task returned by <paramref name="func"/> completes, or is cancelled as <see cref="InvokeAsync(Action, CancellationToken)"/> describes.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	/// <remarks>
	/// Without this overload an async lambda passed with a token would bind to
	/// <see cref="InvokeAsync{TReturn}(Func{TReturn}, CancellationToken)"/>; see <see cref="InvokeAsync(Func{Task})"/>.
	/// </remarks>
	public Task InvokeAsync(Func<Task> func, CancellationToken cancellationToken)
	{
		Ensure.NotNull(func);
		return InvokeAndAwaitCoreAsync(func, cancellationToken);
	}

	// The task handed back completes through a RunContinuationsAsynchronously source. Returning an
	// async method's own task instead would let the caller's code after its await run inline on
	// whichever thread completes the delegate's task, which is often the owner thread (#102).
	private Task<object?> InvokeAndAwaitCoreAsync(Func<Task> func, CancellationToken cancellationToken)
	{
		TaskCompletionSource<object?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_ = ForwardCompletionAsync(func, completion, cancellationToken);
		return completion.Task;
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever the delegate throws is handed to the caller's task.")]
	private async Task ForwardCompletionAsync(Func<Task> func, TaskCompletionSource<object?> completion, CancellationToken cancellationToken)
	{
		try
		{
			Task inner = await InvokeCoreAsync<Task>(func, cancellationToken).ConfigureAwait(false);
			if (await TrySetFaultsAsync(inner, completion).ConfigureAwait(false))
			{
				return;
			}

			await inner.ConfigureAwait(false);
			completion.TrySetResult(null);
		}
		catch (OperationCanceledException ex)
		{
			completion.TrySetCanceled(ex.CancellationToken);
		}
		catch (Exception ex)
		{
			completion.TrySetException(ex);
		}
	}

	/// <summary>
	/// Invokes the specified asynchronous function, waits for the task it returns, and returns its result.
	/// </summary>
	/// <typeparam name="TReturn">The type of the result.</typeparam>
	/// <param name="func">The asynchronous function to invoke.</param>
	/// <returns>A task that completes with the result of the task returned by <paramref name="func"/>.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	/// <remarks>See <see cref="InvokeAsync(Func{Task})"/>.</remarks>
	public Task<TReturn> InvokeAsync<TReturn>(Func<Task<TReturn>> func)
	{
		Ensure.NotNull(func);
		return InvokeAndAwaitCoreAsync(func, CancellationToken.None);
	}

	/// <summary>
	/// Invokes the specified asynchronous function, waits for the task it returns, and returns its result,
	/// unless it is cancelled before the owner thread starts it.
	/// </summary>
	/// <typeparam name="TReturn">The type of the result.</typeparam>
	/// <param name="func">The asynchronous function to invoke.</param>
	/// <param name="cancellationToken">See <see cref="InvokeAsync(Func{Task}, CancellationToken)"/>.</param>
	/// <returns>A task that completes with the result of the task returned by <paramref name="func"/>, or is cancelled as <see cref="InvokeAsync(Action, CancellationToken)"/> describes.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	public Task<TReturn> InvokeAsync<TReturn>(Func<Task<TReturn>> func, CancellationToken cancellationToken)
	{
		Ensure.NotNull(func);
		return InvokeAndAwaitCoreAsync(func, cancellationToken);
	}

	// See InvokeAndAwaitCoreAsync(Func<Task>).
	private Task<TReturn> InvokeAndAwaitCoreAsync<TReturn>(Func<Task<TReturn>> func, CancellationToken cancellationToken)
	{
		TaskCompletionSource<TReturn> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_ = ForwardCompletionAsync(func, completion, cancellationToken);
		return completion.Task;
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever the delegate throws is handed to the caller's task.")]
	private async Task ForwardCompletionAsync<TReturn>(Func<Task<TReturn>> func, TaskCompletionSource<TReturn> completion, CancellationToken cancellationToken)
	{
		try
		{
			Task<TReturn> inner = await InvokeCoreAsync<Task<TReturn>>(func, cancellationToken).ConfigureAwait(false);
			if (await TrySetFaultsAsync(inner, completion).ConfigureAwait(false))
			{
				return;
			}

			completion.TrySetResult(await inner.ConfigureAwait(false));
		}
		catch (OperationCanceledException ex)
		{
			completion.TrySetCanceled(ex.CancellationToken);
		}
		catch (Exception ex)
		{
			completion.TrySetException(ex);
		}
	}

	/// <summary>
	/// Waits for <paramref name="inner"/> and, when it faulted with more than one exception, hands all of
	/// them to <paramref name="completion"/>.
	/// </summary>
	/// <returns>True when <paramref name="completion"/> was given the faults; false to await <paramref name="inner"/> as usual.</returns>
	/// <remarks>
	/// <c>await</c> rethrows only the first of a task's exceptions, so a delegate returning
	/// <c>Task.WhenAll(a, b)</c> would otherwise lose every failure but one. A task with a single exception
	/// still goes through <c>await</c>, which keeps its existing mapping: an
	/// <see cref="OperationCanceledException"/> cancels the caller's task rather than faulting it.
	/// </remarks>
	private static async Task<bool> TrySetFaultsAsync<TResult>(Task? inner, TaskCompletionSource<TResult> completion)
	{
		if (inner is null)
		{
			return false;
		}

		// WhenAny completes when inner does and never throws, on every target framework.
		await Task.WhenAny(inner).ConfigureAwait(false);
		if (inner.Exception is { InnerExceptions.Count: > 1 } aggregate)
		{
			completion.TrySetException(aggregate.InnerExceptions);
			return true;
		}

		return false;
	}

	/// <summary>
	/// Invokes the specified asynchronous function and blocks until the task it returns completes.
	/// </summary>
	/// <param name="func">The asynchronous function to invoke.</param>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	/// <remarks>
	/// The function starts on the owner thread, and the caller blocks until its task has completed, not
	/// just until it reaches its first await. Called on the owner thread, this blocks the owner thread, so
	/// the function must not wait on anything that needs <see cref="DoInvokes"/> to be pumped; prefer
	/// <see cref="InvokeAsync(Func{Task})"/> there.
	/// </remarks>
	// See Invoke(Action): GetResult() keeps the stack trace of the code that actually failed.
	public void Invoke(Func<Task> func) => InvokeAsync(func, CancellationToken.None).GetAwaiter().GetResult();

	/// <summary>
	/// Invokes the specified asynchronous function, blocks until the task it returns completes, and returns its result.
	/// </summary>
	/// <typeparam name="TReturn">The type of the result.</typeparam>
	/// <param name="func">The asynchronous function to invoke.</param>
	/// <returns>The result of the task returned by <paramref name="func"/>.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	/// <remarks>See <see cref="Invoke(Func{Task})"/>.</remarks>
	// See Invoke(Action): GetResult() keeps the stack trace of the code that actually failed.
	public TReturn Invoke<TReturn>(Func<Task<TReturn>> func) => InvokeAsync(func, CancellationToken.None).GetAwaiter().GetResult();

	/// <summary>
	/// Invokes the specified <see cref="ValueTask"/>-returning function and waits for the operation it returns to complete.
	/// </summary>
	/// <param name="func">The asynchronous function to invoke.</param>
	/// <returns>A task that completes when the <see cref="ValueTask"/> returned by <paramref name="func"/> completes.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	/// <remarks>
	/// The function starts on the owner thread, where its <see cref="ValueTask"/> is converted to a
	/// <see cref="Task"/> so it is consumed exactly once. Passed to <c>InvokeAsync</c>, a <see cref="ValueTask"/>-returning
	/// function would bind to <see cref="InvokeAsync{TReturn}(Func{TReturn})"/>, whose task completes at the function's
	/// first incomplete await and never surfaces an exception thrown after it.
	/// This is not an <c>InvokeAsync</c> overload because an async lambda converts equally well to
	/// <see cref="Func{TResult}"/> of <see cref="Task"/> and of <see cref="ValueTask"/>, so the two overloads would be
	/// ambiguous on C# 12 and earlier, the default for net8.0, netstandard and .NET Framework consumers.
	/// </remarks>
	public Task InvokeValueTaskAsync(Func<ValueTask> func)
	{
		Ensure.NotNull(func);
		return InvokeAndAwaitCoreAsync(() => func().AsTask(), CancellationToken.None);
	}

	/// <summary>
	/// Invokes the specified <see cref="ValueTask{TResult}"/>-returning function, waits for the operation it returns, and returns its result.
	/// </summary>
	/// <typeparam name="TReturn">The type of the result.</typeparam>
	/// <param name="func">The asynchronous function to invoke.</param>
	/// <returns>A task that completes with the result of the <see cref="ValueTask{TResult}"/> returned by <paramref name="func"/>.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	/// <remarks>See <see cref="InvokeValueTaskAsync(Func{ValueTask})"/>.</remarks>
	public Task<TReturn> InvokeValueTaskAsync<TReturn>(Func<ValueTask<TReturn>> func)
	{
		Ensure.NotNull(func);
		return InvokeAndAwaitCoreAsync(() => func().AsTask(), CancellationToken.None);
	}

	/// <summary>
	/// Invokes the specified <see cref="ValueTask"/>-returning function and blocks until the operation it returns completes.
	/// </summary>
	/// <param name="func">The asynchronous function to invoke.</param>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	/// <remarks>See <see cref="Invoke(Func{Task})"/>.</remarks>
	// See Invoke(Action): GetResult() keeps the stack trace of the code that actually failed.
	public void InvokeValueTask(Func<ValueTask> func) => InvokeValueTaskAsync(func).GetAwaiter().GetResult();

	/// <summary>
	/// Invokes the specified <see cref="ValueTask{TResult}"/>-returning function, blocks until the operation it returns completes, and returns its result.
	/// </summary>
	/// <typeparam name="TReturn">The type of the result.</typeparam>
	/// <param name="func">The asynchronous function to invoke.</param>
	/// <returns>The result of the <see cref="ValueTask{TResult}"/> returned by <paramref name="func"/>.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	/// <remarks>See <see cref="Invoke(Func{Task})"/>.</remarks>
	// See Invoke(Action): GetResult() keeps the stack trace of the code that actually failed.
	public TReturn InvokeValueTask<TReturn>(Func<ValueTask<TReturn>> func) => InvokeValueTaskAsync(func).GetAwaiter().GetResult();

	/// <summary>
	/// Attempts to queue an action to run on the owner thread without blocking the caller
	/// or allocating.
	/// </summary>
	/// <param name="func">The action to queue.</param>
	/// <returns>
	/// <see langword="true"/> if the action was executed immediately (called on the owner thread outside
	/// <see cref="DoInvokes"/>) or successfully queued; <see langword="false"/> if the bounded queue was full and the action was
	/// dropped.
	/// </returns>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="func"/> is null.</exception>
	/// <remarks>
	/// Unlike <see cref="Invoke(Action)"/> / <see cref="InvokeAsync(Action)"/>, this method never blocks
	/// and never allocates a <see cref="Task"/>: the caller is not notified of completion and cannot
	/// await a result. It is intended for non-real-time producers that want to push work to the owner
	/// thread cheaply. Queued actions run the next time <see cref="DoInvokes"/> is pumped, in the order
	/// they were submitted relative to each other and to work queued through <see cref="Invoke(Action)"/>
	/// and <see cref="InvokeAsync(Action)"/>: a thread that calls <c>InvokeAsync(A)</c> and then
	/// <c>TryBeginInvoke(B)</c> sees A run before B. An action queued while a pump is already running
	/// waits for the pump after it.
	/// When called from the owner thread outside <see cref="DoInvokes"/> the action runs synchronously and
	/// immediately. When called from the owner thread while <see cref="DoInvokes"/> is running, for example
	/// from inside an <see cref="InvokeAsync(Action)"/> delegate or another queued action, it is queued
	/// like any other and runs on the next call to <see cref="DoInvokes"/>, so an action that re-posts
	/// itself runs once per pump instead of recursing.
	/// <para>
	/// A throwing action is reported the same way whichever thread called this method, and its exception
	/// never comes back out of <see cref="TryBeginInvoke(Action)"/> to the code that called it. A queued
	/// action's failure is reported by the pump that runs it. An action run inline on the owner thread,
	/// outside a pump, is reported at once: <see cref="BeginInvokeFailed"/> is raised, or, with no handler
	/// attached, an <see cref="AggregateException"/> is thrown, as <see cref="DoInvokes"/> would.
	/// </para>
	/// <para>
	/// After <see cref="Shutdown"/>, a call from another thread queues nothing and returns
	/// <see langword="false"/>.
	/// </para>
	/// </remarks>
	/// <exception cref="AggregateException">Thrown on the owner thread, outside <see cref="DoInvokes"/>, when the action threw and no <see cref="BeginInvokeFailed"/> handler is attached, or when the handler threw.</exception>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A fire-and-forget action can throw anything, and every failure is reported through BeginInvokeFailed or an AggregateException, as queued actions' failures are.")]
	public bool TryBeginInvoke(Action func)
	{
		Ensure.NotNull(func);

		// On the owner thread while DoInvokes is running, queue the action for the next pump rather than
		// run it inline. Every action the pump runs is on the owner thread, so running inline here would
		// let an action that re-posts itself recurse inside one DoInvokes call until the stack overflows.
		if (IsOwnerThread && !isPumping)
		{
			// The action did run, so this still returns true. Its exception is reported as a queued
			// action's would be, rather than thrown into whichever code happened to call this here: the
			// owner's loop, or an InvokeAsync delegate whose caller would get back another action's failure.
			try
			{
				func();
			}
			catch (Exception ex)
			{
				ReportBeginInvokeFailures([ex]);
			}

			return true;
		}

		return !IsShutDown && BeginInvokeQueue.TryEnqueue((NextSequence(), func));
	}

	/// <summary>
	/// Occurs on the owner thread, from within <see cref="DoInvokes"/>, for each action queued with
	/// <see cref="TryBeginInvoke(Action)"/> that threw.
	/// </summary>
	/// <remarks>
	/// Raised only after the pump has run all the work it took on, so a failing action never delays
	/// other queued work. An action that <see cref="TryBeginInvoke(Action)"/> ran inline on the owner
	/// thread, outside <see cref="DoInvokes"/>, is reported here too, from
	/// <see cref="TryBeginInvoke(Action)"/> itself. When no handler is attached, <see cref="DoInvokes"/> throws an
	/// <see cref="AggregateException"/> instead, so the failure is never silently lost. Each failure is
	/// raised on its own, and to each subscriber on its own, so a handler that throws still receives every
	/// later failure, and the subscribers after it still receive the failure it threw on; what it threw is
	/// then surfaced by <see cref="DoInvokes"/> as an <see cref="AggregateException"/>.
	/// </remarks>
	public event EventHandler<BeginInvokeFailedEventArgs>? BeginInvokeFailed;

	/// <summary>
	/// Executes all queued tasks synchronously on the thread that created the Invoker instance.
	/// </summary>
	/// <remarks>
	/// Each call runs the work that was already queued when it started, from both queues, in the order
	/// it was submitted, and then returns. Work queued while it runs, including by the actions it runs,
	/// waits for the next call, so a producer that never stops posting cannot keep one call from returning. Exceptions from
	/// <see cref="Invoke(Action)"/> and <see cref="InvokeAsync(Action)"/> work go back to their own
	/// callers. Exceptions from <see cref="TryBeginInvoke(Action)"/> actions are reported through
	/// <see cref="BeginInvokeFailed"/> once that work has run.
	/// </remarks>
	/// <exception cref="InvalidOperationException">Thrown when this method is called on a different thread than the one that created the Invoker instance.</exception>
	/// <exception cref="AggregateException">Thrown after the queued work has run when one or more <see cref="TryBeginInvoke(Action)"/> actions threw and no <see cref="BeginInvokeFailed"/> handler is attached, or when a handler threw. In the latter case every failure has still been raised to the handler, and each handler exception is followed in <see cref="AggregateException.InnerExceptions"/> by the failure it was handling.</exception>
	public void DoInvokes()
	{
		if (!IsOwnerThread)
		{
			throw new InvalidOperationException("This method must be called on the thread that created the Invoker instance.");
		}

		if (IsShutDown)
		{
			// Anything that slipped into a queue while Shutdown ran is dropped, not run late.
			DiscardQueuedWork();
			return;
		}

		// Run only the work that was queued when the pump started. Everything dequeued frees a slot, so
		// a producer posting at least as fast as the owner runs its actions would otherwise keep this
		// call from ever returning, freezing the owner's loop and starving the task queue below.
		int beginInvokeBudget = BeginInvokeQueue.Count;
		int taskBudget = TaskQueue.Count;

		// A fire-and-forget action has no caller to hand its exception back to, so RunPendingWork catches
		// it rather than let it abort the pump: an escaping exception would skip the rest of both
		// queues and leave every thread blocked in Invoke waiting for a pump that may never come.
		// isPumping also makes owner-thread TryBeginInvoke calls made by the work this pump runs queue
		// their action for the next pump instead of running it inline. A pump started from inside one
		// of those actions keeps its own failure list, and the outer pump's list is restored when it returns.
		bool outerIsPumping = isPumping;
		List<Exception>? outerFailures = pumpFailures;
		isPumping = true;
		pumpFailures = null;
		List<Exception>? failures;
		try
		{
			RunPendingWork(beginInvokeBudget, taskBudget);
		}
		finally
		{
			failures = pumpFailures;
			pumpFailures = outerFailures;
			isPumping = outerIsPumping;
		}

		if (failures is not null)
		{
			ReportBeginInvokeFailures(failures);
		}
	}

	/// <summary>
	/// Runs the work <see cref="DoInvokes"/> took on, collecting <see cref="TryBeginInvoke(Action)"/>
	/// failures into <see cref="pumpFailures"/>.
	/// </summary>
	/// <param name="beginInvokeBudget">How many <see cref="TryBeginInvoke(Action)"/> actions this pump may run.</param>
	/// <param name="taskBudget">How many <see cref="Invoke(Action)"/> and <see cref="InvokeAsync(Action)"/> tasks this pump may run.</param>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A fire-and-forget action can throw anything, and every failure is reported through BeginInvokeFailed or an AggregateException once the pump's work has run.")]
	private void RunPendingWork(int beginInvokeBudget, int taskBudget)
	{
		// Merge the two queues by submission number. Each queue is FIFO, and a thread takes its
		// number before it enqueues, so taking the lower-numbered head each time runs one thread's
		// work in the order that thread submitted it, whichever API it went through.
		bool haveAction = TryTakeAction(ref beginInvokeBudget, out (long Sequence, Action Action) nextAction);
		bool haveTask = TryTakeTask(ref taskBudget, out (long Sequence, Task Task) nextTask);
		while (haveAction || haveTask)
		{
			if (haveAction && (!haveTask || nextAction.Sequence < nextTask.Sequence))
			{
				try
				{
					nextAction.Action();
				}
				catch (Exception ex)
				{
					(pumpFailures ??= []).Add(ex);
				}

				haveAction = TryTakeAction(ref beginInvokeBudget, out nextAction);
			}
			else
			{
				RunQueuedTask(nextTask.Task);
				haveTask = TryTakeTask(ref taskBudget, out nextTask);
			}
		}
	}

	/// <summary>
	/// Raises <see cref="BeginInvokeFailed"/> for each failure, or throws them as an
	/// <see cref="AggregateException"/> when no handler is attached.
	/// </summary>
	/// <param name="failures">The exceptions thrown by <see cref="TryBeginInvoke(Action)"/> actions during one pump.</param>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A BeginInvokeFailed handler can throw anything, and each such exception is surfaced in the AggregateException thrown at the end.")]
	private void ReportBeginInvokeFailures(List<Exception> failures)
	{
		// One snapshot for both the null check and the calls: re-reading the event could see the last
		// handler unsubscribe in between, and then no handler would run and nothing would be thrown.
		EventHandler<BeginInvokeFailedEventArgs> handler = BeginInvokeFailed
			?? throw new AggregateException("One or more actions queued with TryBeginInvoke threw.", failures);

		// A handler that throws must not cost the failures after it, or the subscribers after it, their
		// report. Invoking the multicast delegate directly would stop at the first subscriber that threw,
		// so each subscriber is called on its own for each failure, and anything one throws is surfaced,
		// with the failure it was handling, at the end.
		Delegate[] subscribers = handler.GetInvocationList();
		List<Exception>? handlerFailures = null;
		foreach (Exception failure in failures)
		{
			BeginInvokeFailedEventArgs args = new(failure);
			foreach (EventHandler<BeginInvokeFailedEventArgs> subscriber in subscribers.Cast<EventHandler<BeginInvokeFailedEventArgs>>())
			{
				try
				{
					subscriber(this, args);
				}
				catch (Exception ex)
				{
					(handlerFailures ??= []).AddRange([ex, failure]);
				}
			}
		}

		if (handlerFailures is not null)
		{
			throw new AggregateException("A BeginInvokeFailed handler threw. Each handler exception is followed by the TryBeginInvoke failure it was handling.", handlerFailures);
		}
	}

	/// <summary>
	/// Releases every caller still waiting on queued work and stops the invoker accepting more.
	/// </summary>
	/// <remarks>
	/// Call this from the owner thread when its loop ends, before signalling and joining the threads that
	/// call <see cref="Invoke(Action)"/>. Every queued <see cref="Invoke(Action)"/> and
	/// <see cref="InvokeAsync(Action)"/> call completes with an <see cref="OperationCanceledException"/>
	/// and its delegate never runs. Queued <see cref="TryBeginInvoke(Action)"/> actions are dropped.
	/// Afterwards, <c>Invoke</c> and <c>InvokeAsync</c> from another thread fail at once with an
	/// <see cref="OperationCanceledException"/>, <see cref="TryBeginInvoke(Action)"/> from another thread
	/// returns <see langword="false"/>, and <see cref="DoInvokes"/> runs nothing. Calls made on the owner
	/// thread still run inline, as they always do. Calling this more than once has no further effect.
	/// A call from another thread that races with this one can instead fail with an
	/// <see cref="ObjectDisposedException"/>; either way its delegate never runs.
	/// </remarks>
	/// <exception cref="InvalidOperationException">Thrown when this method is called on a different thread than the one that created the Invoker instance.</exception>
	public void Shutdown()
	{
		if (!IsOwnerThread)
		{
			throw new InvalidOperationException("This method must be called on the thread that created the Invoker instance.");
		}

		// Every queued task carries this token or one linked to it, so this cancels them all and releases
		// their callers. Their continuations run asynchronously, so none of the callers' code runs here.
		if (!IsShutDown)
		{
			shutdownSource.Cancel();

			// Nothing is registered on the source any more that the cancellation did not already run,
			// and every later read of its token is guarded by IsShutDown.
			shutdownSource.Dispose();
		}

		DiscardQueuedWork();
	}

	/// <summary>
	/// Empties both queues without running anything. Every task in them has already been cancelled.
	/// </summary>
	private void DiscardQueuedWork()
	{
		while (TaskQueue.TryDequeue(out _))
		{
			// Intentionally empty: dequeuing is the whole job, and each task is already cancelled.
		}

		while (BeginInvokeQueue.TryDequeue(out _))
		{
			// Intentionally empty: a dropped action is never run.
		}
	}

	/// <summary>
	/// Picks the token a queued task is created with: the shutdown token, or, when the caller passed a
	/// token that can be cancelled, a token cancelled by either.
	/// </summary>
	/// <param name="cancellationToken">The caller's token.</param>
	/// <param name="queueToken">The token to create the queued task with.</param>
	/// <returns>The linked source to dispose once the task has completed, or <see langword="null"/> when none was needed.</returns>
	private CancellationTokenSource? LinkToShutdown(CancellationToken cancellationToken, out CancellationToken queueToken)
	{
		if (IsShutDown)
		{
			// Already cancelled, so the task is created cancelled and never queued.
			queueToken = new CancellationToken(canceled: true);
			return null;
		}

		// Shutdown can still win the race between the check above and this read. The read then throws
		// ObjectDisposedException, which reaches the caller as the shutdown's report instead of an
		// OperationCanceledException.
		CancellationToken shutdownToken = shutdownSource.Token;
		if (!cancellationToken.CanBeCanceled)
		{
			queueToken = shutdownToken;
			return null;
		}

		CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownToken);
		queueToken = linked.Token;
		return linked;
	}

	/// <summary>
	/// Queues a task for <see cref="DoInvokes"/>, unless it was cancelled before it could be queued.
	/// </summary>
	private void Enqueue(Task task)
	{
		// A task created with a token that is already cancelled starts out cancelled, so after Shutdown, or
		// with a token cancelled up front, the caller is released at once and nothing waits in the queue.
		if (!task.IsCompleted)
		{
			TaskQueue.Enqueue((NextSequence(), task));
		}
	}

	/// <summary>
	/// Builds the exception a caller sees when its queued work was withdrawn before it ran.
	/// </summary>
	/// <param name="cancellationToken">The caller's token.</param>
	private static OperationCanceledException WithdrawnException(CancellationToken cancellationToken) =>
		cancellationToken.IsCancellationRequested
			? new OperationCanceledException(cancellationToken)
			: new OperationCanceledException("The invoker was shut down before it ran this work.", new CancellationToken(canceled: true));

	/// <summary>
	/// Runs a queued task inline on the owner thread, unless it was cancelled while it waited.
	/// </summary>
	private static void RunQueuedTask(Task task)
	{
		// The default scheduler always runs the task inline here. With no argument it would be
		// TaskScheduler.Current, and when DoInvokes is itself called from a task on a scheduler
		// that refuses to inline, the task was queued back to that scheduler: it ran off the
		// owner thread, or never ran at all if the owner was that scheduler's only thread.
		// A token can cancel the task between the check and the start, and the task then refuses to
		// start; it has a caller to report that to already, so the pump moves on.
		if (task.IsCompleted)
		{
			return;
		}

		try
		{
			task.RunSynchronously(TaskScheduler.Default);
		}
		catch (InvalidOperationException) when (task.IsCanceled)
		{
			// Intentionally swallowed: the token won the race, and the task's caller already sees it cancelled.
		}
	}

	/// <summary>
	/// Hands out the next submission number.
	/// </summary>
	private long NextSequence() => Interlocked.Increment(ref submissionSequence);

	/// <summary>
	/// Dequeues the next <see cref="TryBeginInvoke(Action)"/> action, if the pump's budget allows one.
	/// </summary>
	private bool TryTakeAction(ref int budget, out (long Sequence, Action Action) item)
	{
		if (budget > 0 && BeginInvokeQueue.TryDequeue(out item))
		{
			budget--;
			return true;
		}

		item = default;
		return false;
	}

	/// <summary>
	/// Dequeues the next <see cref="Invoke(Action)"/> or <see cref="InvokeAsync(Action)"/> task, if the pump's budget allows one.
	/// </summary>
	private bool TryTakeTask(ref int budget, out (long Sequence, Task Task) item)
	{
		if (budget > 0 && TaskQueue.TryDequeue(out item))
		{
			budget--;
			return true;
		}

		item = default;
		return false;
	}
}
