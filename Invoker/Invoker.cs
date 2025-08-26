// Copyright (c) ktsu.dev
// All rights reserved.
// Licensed under the MIT license.

namespace ktsu.Invoker;

using System.Collections.Concurrent;

/// <summary>
/// Provides methods to invoke actions and functions asynchronously or synchronously on a specific thread.
/// </summary>
public class Invoker
{
	/// <summary>
	/// Gets the ID of the thread on which this instance was created.
	/// </summary>
	private int ThreadId { get; } = Environment.CurrentManagedThreadId;

	/// <summary>
	/// Gets the queue of tasks to be executed.
	/// </summary>
	internal ConcurrentQueue<Task> TaskQueue { get; } = new();

	/// <summary>
	/// Invokes the specified action asynchronously.
	/// </summary>
	/// <param name="func">The action to invoke.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	/// <exception cref="ArgumentNullException">Thrown when the action is null.</exception>
	public async Task InvokeAsync(Action func)
	{
		Guard.NotNull(func);

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
		Guard.NotNull(func);

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
	/// Executes all queued tasks synchronously on the thread that created the Invoker instance.
	/// </summary>
	/// <exception cref="InvalidOperationException">Thrown when this method is called on a different thread than the one that created the Invoker instance.</exception>
	public void DoInvokes()
	{
		if (ThreadId != Environment.CurrentManagedThreadId)
		{
			throw new InvalidOperationException("This method must be called on the thread that created the Invoker instance.");
		}

		while (TaskQueue.TryDequeue(out Task? task))
		{
			task.RunSynchronously();
		}
	}
}
