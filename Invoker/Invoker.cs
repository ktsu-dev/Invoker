// Copyright (c) ktsu.dev
// All rights reserved.
// Licensed under the MIT license.

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
/// <param name="beginInvokeCapacity">The capacity of the non-blocking <see cref="TryBeginInvoke(Action)"/> queue. Rounded up to the next power of two.</param>
/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="beginInvokeCapacity"/> is less than one.</exception>
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

		Task task = new(func);
		TaskQueue.Enqueue(task);
		await task.ConfigureAwait(false);
	}

	/// <summary>
	/// Invokes the specified action synchronously.
	/// </summary>
	/// <param name="func">The action to invoke.</param>
	/// <exception cref="ArgumentNullException">Thrown when the action is null.</exception>
	public void Invoke(Action func)
	{
		try
		{
			InvokeAsync(func).Wait();
		}
		catch (AggregateException ex)
		{
			throw ex.InnerException!;
		}
	}

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

		Task<TReturn> task = new(func);
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
	public TReturn Invoke<TReturn>(Func<TReturn> func)
	{
		try
		{
			return InvokeAsync(func).Result;
		}
		catch (AggregateException ex)
		{
			throw ex.InnerException!;
		}
	}

	/// <summary>
	/// Attempts to queue an action for fire-and-forget execution on the owner thread without blocking
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
	/// thread cheaply. Queued actions run the next time <see cref="DoInvokes"/> is pumped, in FIFO order.
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
	/// Executes all queued tasks synchronously on the thread that created the Invoker instance.
	/// </summary>
	/// <exception cref="InvalidOperationException">Thrown when this method is called on a different thread than the one that created the Invoker instance.</exception>
	public void DoInvokes()
	{
		if (ThreadId != Environment.CurrentManagedThreadId)
		{
			throw new InvalidOperationException("This method must be called on the thread that created the Invoker instance.");
		}

		while (BeginInvokeQueue.TryDequeue(out Action? action))
		{
			action!();
		}

		while (TaskQueue.TryDequeue(out Task? task))
		{
			task.RunSynchronously();
		}
	}
}
