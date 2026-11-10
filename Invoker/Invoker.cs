// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.Invoker;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

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
		return InvokeCoreAsync(func);
	}

	private async Task InvokeCoreAsync(Action func)
	{
		if (IsOwnerThread)
		{
			func();
			return;
		}

		// RunContinuationsAsynchronously keeps the caller's code after the await off the owner thread:
		// without it, DoInvokes' RunSynchronously would run that code inline, inside the pump.
		// DenyChildAttach stops an AttachedToParent child the delegate starts from holding up or faulting
		// this task, which the owner-thread fast path, with no task to attach to, never does.
		Task task = new(func, QueuedTaskOptions);
		TaskQueue.Enqueue((NextSequence(), task));
		await task.ConfigureAwait(false);
	}

	/// <summary>
	/// Invokes the specified action synchronously.
	/// </summary>
	/// <param name="func">The action to invoke.</param>
	/// <exception cref="ArgumentNullException">Thrown when the action is null.</exception>
	// GetAwaiter().GetResult() rethrows the delegate's own exception with its stack trace intact,
	// where unwrapping the AggregateException from Wait() and rethrowing it would reset the trace.
	public void Invoke(Action func) => InvokeAsync(func).GetAwaiter().GetResult();

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
		return InvokeCoreAsync(func);
	}

	private async Task<TReturn> InvokeCoreAsync<TReturn>(Func<TReturn> func)
	{
		if (IsOwnerThread)
		{
			return func();
		}

		// See InvokeAsync(Action): keep the caller's continuation out of DoInvokes.
		Task<TReturn> task = new(func, QueuedTaskOptions);
		TaskQueue.Enqueue((NextSequence(), task));
		return await task.ConfigureAwait(false);
	}

	/// <summary>
	/// Invokes the specified function synchronously and returns the result.
	/// </summary>
	/// <typeparam name="TReturn">The type of the return value.</typeparam>
	/// <param name="func">The function to invoke.</param>
	/// <returns>The result of the function.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	// See Invoke(Action): GetResult() keeps the stack trace of the code that actually failed.
	public TReturn Invoke<TReturn>(Func<TReturn> func) => InvokeAsync(func).GetAwaiter().GetResult();

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
	[OverloadResolutionPriority(1)]
	public Task InvokeAsync(Func<Task> func)
	{
		Ensure.NotNull(func);
		return InvokeAndAwaitCoreAsync(func);
	}

	// The task handed back completes through a RunContinuationsAsynchronously source. Returning an
	// async method's own task instead would let the caller's code after its await run inline on
	// whichever thread completes the delegate's task, which is often the owner thread (#102).
	private Task<object?> InvokeAndAwaitCoreAsync(Func<Task> func)
	{
		TaskCompletionSource<object?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_ = ForwardCompletionAsync(func, completion);
		return completion.Task;
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever the delegate throws is handed to the caller's task.")]
	private async Task ForwardCompletionAsync(Func<Task> func, TaskCompletionSource<object?> completion)
	{
		try
		{
			Task inner = await InvokeCoreAsync<Task>(func).ConfigureAwait(false);
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
	[OverloadResolutionPriority(1)]
	public Task<TReturn> InvokeAsync<TReturn>(Func<Task<TReturn>> func)
	{
		Ensure.NotNull(func);
		return InvokeAndAwaitCoreAsync(func);
	}

	// See InvokeAndAwaitCoreAsync(Func<Task>).
	private Task<TReturn> InvokeAndAwaitCoreAsync<TReturn>(Func<Task<TReturn>> func)
	{
		TaskCompletionSource<TReturn> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_ = ForwardCompletionAsync(func, completion);
		return completion.Task;
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever the delegate throws is handed to the caller's task.")]
	private async Task ForwardCompletionAsync<TReturn>(Func<Task<TReturn>> func, TaskCompletionSource<TReturn> completion)
	{
		try
		{
			Task<TReturn> inner = await InvokeCoreAsync<Task<TReturn>>(func).ConfigureAwait(false);
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
	[OverloadResolutionPriority(1)]
	public void Invoke(Func<Task> func) => InvokeAsync(func).GetAwaiter().GetResult();

	/// <summary>
	/// Invokes the specified asynchronous function, blocks until the task it returns completes, and returns its result.
	/// </summary>
	/// <typeparam name="TReturn">The type of the result.</typeparam>
	/// <param name="func">The asynchronous function to invoke.</param>
	/// <returns>The result of the task returned by <paramref name="func"/>.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	/// <remarks>See <see cref="Invoke(Func{Task})"/>.</remarks>
	// See Invoke(Action): GetResult() keeps the stack trace of the code that actually failed.
	[OverloadResolutionPriority(1)]
	public TReturn Invoke<TReturn>(Func<Task<TReturn>> func) => InvokeAsync(func).GetAwaiter().GetResult();

	/// <summary>
	/// Invokes the specified <see cref="ValueTask"/>-returning function and waits for the operation it returns to complete.
	/// </summary>
	/// <param name="func">The asynchronous function to invoke.</param>
	/// <returns>A task that completes when the <see cref="ValueTask"/> returned by <paramref name="func"/> completes.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	/// <remarks>
	/// The function starts on the owner thread, where its <see cref="ValueTask"/> is converted to a
	/// <see cref="Task"/> so it is consumed exactly once. Without this overload the function would bind to
	/// <see cref="InvokeAsync{TReturn}(Func{TReturn})"/>, whose task completes at the function's first
	/// incomplete await and never surfaces an exception thrown after it.
	/// </remarks>
	public Task InvokeAsync(Func<ValueTask> func)
	{
		Ensure.NotNull(func);
		return InvokeAndAwaitCoreAsync(() => func().AsTask());
	}

	/// <summary>
	/// Invokes the specified <see cref="ValueTask{TResult}"/>-returning function, waits for the operation it returns, and returns its result.
	/// </summary>
	/// <typeparam name="TReturn">The type of the result.</typeparam>
	/// <param name="func">The asynchronous function to invoke.</param>
	/// <returns>A task that completes with the result of the <see cref="ValueTask{TResult}"/> returned by <paramref name="func"/>.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	/// <remarks>See <see cref="InvokeAsync(Func{ValueTask})"/>.</remarks>
	public Task<TReturn> InvokeAsync<TReturn>(Func<ValueTask<TReturn>> func)
	{
		Ensure.NotNull(func);
		return InvokeAndAwaitCoreAsync(() => func().AsTask());
	}

	/// <summary>
	/// Invokes the specified <see cref="ValueTask"/>-returning function and blocks until the operation it returns completes.
	/// </summary>
	/// <param name="func">The asynchronous function to invoke.</param>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	/// <remarks>See <see cref="Invoke(Func{Task})"/>.</remarks>
	// See Invoke(Action): GetResult() keeps the stack trace of the code that actually failed.
	public void Invoke(Func<ValueTask> func) => InvokeAsync(func).GetAwaiter().GetResult();

	/// <summary>
	/// Invokes the specified <see cref="ValueTask{TResult}"/>-returning function, blocks until the operation it returns completes, and returns its result.
	/// </summary>
	/// <typeparam name="TReturn">The type of the result.</typeparam>
	/// <param name="func">The asynchronous function to invoke.</param>
	/// <returns>The result of the <see cref="ValueTask{TResult}"/> returned by <paramref name="func"/>.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	/// <remarks>See <see cref="Invoke(Func{Task})"/>.</remarks>
	// See Invoke(Action): GetResult() keeps the stack trace of the code that actually failed.
	public TReturn Invoke<TReturn>(Func<ValueTask<TReturn>> func) => InvokeAsync(func).GetAwaiter().GetResult();

	/// <summary>
	/// Attempts to queue an action to run on the owner thread without blocking the caller
	/// or allocating.
	/// </summary>
	/// <param name="func">The action to queue.</param>
	/// <returns>
	/// <see langword="true"/> if the action was executed immediately (called on the owner thread) or
	/// successfully queued; <see langword="false"/> if the bounded queue was full and the action was
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
	/// When called from the owner thread the action runs synchronously and immediately.
	/// </remarks>
	public bool TryBeginInvoke(Action func)
	{
		Ensure.NotNull(func);

		if (IsOwnerThread)
		{
			func();
			return true;
		}

		return BeginInvokeQueue.TryEnqueue((NextSequence(), func));
	}

	/// <summary>
	/// Occurs on the owner thread, from within <see cref="DoInvokes"/>, for each action queued with
	/// <see cref="TryBeginInvoke(Action)"/> that threw.
	/// </summary>
	/// <remarks>
	/// Raised only after the pump has run all the work it took on, so a failing action never delays
	/// other queued work. When no handler is attached, <see cref="DoInvokes"/> throws an
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
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A fire-and-forget action or a BeginInvokeFailed handler can throw anything, and every failure is reported through BeginInvokeFailed or an AggregateException once the pump's work has run.")]
	public void DoInvokes()
	{
		if (!IsOwnerThread)
		{
			throw new InvalidOperationException("This method must be called on the thread that created the Invoker instance.");
		}

		// Run only the work that was queued when the pump started. Everything dequeued frees a slot, so
		// a producer posting at least as fast as the owner runs its actions would otherwise keep this
		// call from ever returning, freezing the owner's loop and starving the task queue below.
		int beginInvokeBudget = BeginInvokeQueue.Count;
		int taskBudget = TaskQueue.Count;

		// A fire-and-forget action has no caller to hand its exception back to, so catch it here
		// rather than let it abort the pump: an escaping exception would skip the rest of both
		// queues and leave every thread blocked in Invoke waiting for a pump that may never come.
		List<Exception>? failures = null;

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
					(failures ??= []).Add(ex);
				}

				haveAction = TryTakeAction(ref beginInvokeBudget, out nextAction);
			}
			else
			{
				// The default scheduler always runs the task inline here. With no argument it would be
				// TaskScheduler.Current, and when DoInvokes is itself called from a task on a scheduler
				// that refuses to inline, the task was queued back to that scheduler: it ran off the
				// owner thread, or never ran at all if the owner was that scheduler's only thread.
				nextTask.Task.RunSynchronously(TaskScheduler.Default);
				haveTask = TryTakeTask(ref taskBudget, out nextTask);
			}
		}

		if (failures is not null)
		{
			ReportBeginInvokeFailures(failures);
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
