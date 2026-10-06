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
public class Invoker(int beginInvokeCapacity)
{
	/// <summary>
	/// The default capacity of the non-blocking <see cref="TryBeginInvoke(Action)"/> queue.
	/// </summary>
	private const int DefaultBeginInvokeCapacity = 1024;

	/// <summary>
	/// Gets the ID of the thread on which this instance was created.
	/// </summary>
	private int ThreadId { get; } = Environment.CurrentManagedThreadId;

	/// <summary>
	/// Gets the queue of tasks to be executed.
	/// </summary>
	internal ConcurrentQueue<Task> TaskQueue { get; } = new();

	/// <summary>
	/// Gets the bounded, lock-free queue backing <see cref="TryBeginInvoke(Action)"/>.
	/// </summary>
	private BoundedMpscQueue<Action> BeginInvokeQueue { get; } = new(beginInvokeCapacity);

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
	public async Task InvokeAsync(Action func)
	{
		Ensure.NotNull(func);

		if (ThreadId == Environment.CurrentManagedThreadId)
		{
			func();
			return;
		}

		// RunContinuationsAsynchronously keeps the caller's code after the await off the owner thread:
		// without it, DoInvokes' RunSynchronously would run that code inline, inside the pump.
		Task task = new(func, TaskCreationOptions.RunContinuationsAsynchronously);
		TaskQueue.Enqueue(task);
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
	public async Task<TReturn> InvokeAsync<TReturn>(Func<TReturn> func)
	{
		Ensure.NotNull(func);

		if (ThreadId == Environment.CurrentManagedThreadId)
		{
			return func();
		}

		// See InvokeAsync(Action): keep the caller's continuation out of DoInvokes.
		Task<TReturn> task = new(func, TaskCreationOptions.RunContinuationsAsynchronously);
		TaskQueue.Enqueue(task);
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
	public async Task InvokeAsync(Func<Task> func)
	{
		Ensure.NotNull(func);

		Task inner = await InvokeAsync<Task>(func).ConfigureAwait(false);
		await inner.ConfigureAwait(false);
	}

	/// <summary>
	/// Invokes the specified asynchronous function, waits for the task it returns, and returns its result.
	/// </summary>
	/// <typeparam name="TReturn">The type of the result.</typeparam>
	/// <param name="func">The asynchronous function to invoke.</param>
	/// <returns>A task that completes with the result of the task returned by <paramref name="func"/>.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
	/// <remarks>See <see cref="InvokeAsync(Func{Task})"/>.</remarks>
	public async Task<TReturn> InvokeAsync<TReturn>(Func<Task<TReturn>> func)
	{
		Ensure.NotNull(func);

		Task<TReturn> inner = await InvokeAsync<Task<TReturn>>(func).ConfigureAwait(false);
		return await inner.ConfigureAwait(false);
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
	public TReturn Invoke<TReturn>(Func<Task<TReturn>> func) => InvokeAsync(func).GetAwaiter().GetResult();

	/// <summary>
	/// Attempts to queue an action
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
	/// thread cheaply. Queued actions run the next time <see cref="DoInvokes"/> is pumped, in FIFO order;
	/// an action queued while a pump is already running waits for the pump after it.
	/// When called from the owner thread the action runs synchronously and immediately.
	/// </remarks>
	public bool TryBeginInvoke(Action func)
	{
		Ensure.NotNull(func);

		if (ThreadId == Environment.CurrentManagedThreadId)
		{
			func();
			return true;
		}

		return BeginInvokeQueue.TryEnqueue(func);
	}

	/// <summary>
	/// Occurs on the owner thread, from within <see cref="DoInvokes"/>, for each action queued with
	/// <see cref="TryBeginInvoke(Action)"/> that threw.
	/// </summary>
	/// <remarks>
	/// Raised only after the pump has run all the work it took on, so a failing action never delays
	/// other queued work. When no handler is attached, <see cref="DoInvokes"/> throws an
	/// <see cref="AggregateException"/> instead, so the failure is never silently lost.
	/// </remarks>
	public event EventHandler<BeginInvokeFailedEventArgs>? BeginInvokeFailed;

	/// <summary>
	/// Executes all queued tasks synchronously on the thread that created the Invoker instance.
	/// </summary>
	/// <remarks>
	/// Each call runs the work that was already queued when it started, from both queues, and then
	/// returns. Work queued while it runs, including by the actions it runs, waits for the next call,
	/// so a producer that never stops posting cannot keep one call from returning. Exceptions from
	/// <see cref="Invoke(Action)"/> and <see cref="InvokeAsync(Action)"/> work go back to their own
	/// callers. Exceptions from <see cref="TryBeginInvoke(Action)"/> actions are reported through
	/// <see cref="BeginInvokeFailed"/> once that work has run.
	/// </remarks>
	/// <exception cref="InvalidOperationException">Thrown when this method is called on a different thread than the one that created the Invoker instance.</exception>
	/// <exception cref="AggregateException">Thrown after the queued work has run when one or more <see cref="TryBeginInvoke(Action)"/> actions threw and no <see cref="BeginInvokeFailed"/> handler is attached.</exception>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A fire-and-forget action can throw anything, and every failure is reported through BeginInvokeFailed or an AggregateException once the pump's work has run.")]
	public void DoInvokes()
	{
		if (ThreadId != Environment.CurrentManagedThreadId)
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
		for (int i = 0; i < beginInvokeBudget && BeginInvokeQueue.TryDequeue(out Action? action); i++)
		{
			try
			{
				action!();
			}
			catch (Exception ex)
			{
				(failures ??= []).Add(ex);
			}
		}

		for (int i = 0; i < taskBudget && TaskQueue.TryDequeue(out Task? task); i++)
		{
			// The default scheduler always runs the task inline here. With no argument it would be
			// TaskScheduler.Current, and when DoInvokes is itself called from a task on a scheduler
			// that refuses to inline, the task was queued back to that scheduler: it ran off the
			// owner thread, or never ran at all if the owner was that scheduler's only thread.
			task.RunSynchronously(TaskScheduler.Default);
		}

		if (failures is null)
		{
			return;
		}

		if (BeginInvokeFailed is null)
		{
			throw new AggregateException("One or more actions queued with TryBeginInvoke threw.", failures);
		}

		foreach (Exception failure in failures)
		{
			BeginInvokeFailed?.Invoke(this, new BeginInvokeFailedEventArgs(failure));
		}
	}
}
