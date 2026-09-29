// Copyright (c) 2023-2026 ktsu-dev contributors

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace ktsu.Invoker.Test;

using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class InvokerTests
{
	[TestMethod]
	public async Task InvokeAsyncActionNullShouldThrowArgumentNullException()
	{
		Invoker invoker = new();
		await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await invoker.InvokeAsync(null!).ConfigureAwait(false)).ConfigureAwait(false);
	}

	[TestMethod]
	public async Task InvokeAsyncSameThreadShouldInvokeImmediately()
	{
		Invoker invoker = new();
		bool invoked = false;
		await invoker.InvokeAsync(() => invoked = true).ConfigureAwait(false);
		Assert.IsTrue(invoked, "Action should be invoked immediately on same thread.");
	}

	[TestMethod]
	public void InvokeActionNullShouldThrowArgumentNullException()
	{
		Invoker invoker = new();
		Assert.ThrowsExactly<ArgumentNullException>(() => invoker.Invoke(null!));
	}

	[TestMethod]
	public void InvokeSameThreadShouldInvokeImmediately()
	{
		Invoker invoker = new();
		bool invoked = false;
		invoker.Invoke(() => invoked = true);
		Assert.IsTrue(invoked, "Action should be invoked immediately on same thread.");
	}

	[TestMethod]
	public async Task InvokeAsyncFunctionNullShouldThrowArgumentNullException()
	{
		Invoker invoker = new();
		await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await invoker.InvokeAsync((Func<int>)null!).ConfigureAwait(false)).ConfigureAwait(false);
	}

	[TestMethod]
	public async Task InvokeAsyncFunctionShouldReturnValue()
	{
		Invoker invoker = new();
		int result = await invoker.InvokeAsync(() => 42).ConfigureAwait(false);
		Assert.AreEqual(42, result, "Function should return correct value.");
	}

	[TestMethod]
	public void InvokeFunctionNullShouldThrowArgumentNullException()
	{
		Invoker invoker = new();
		Assert.ThrowsExactly<ArgumentNullException>(() => invoker.Invoke<int>(null!));
	}

	[TestMethod]
	public void InvokeFunctionShouldReturnValue()
	{
		Invoker invoker = new();
		int result = invoker.Invoke(() => 42);
		Assert.AreEqual(42, result, "Function should return correct value.");
	}

	[TestMethod]
	public void DoInvokesDifferentThreadShouldThrowInvalidOperationException()
	{
		Invoker invoker = new();
		Exception? ex = null;
		Thread thread = new(() =>
		{
			try
			{
				invoker.DoInvokes();
			}
			catch (InvalidOperationException e)
			{
				ex = e;
			}
		});
		thread.Start();
		thread.Join();
		Assert.IsNotNull(ex);
		Assert.IsInstanceOfType<InvalidOperationException>(ex);
	}

	[TestMethod]
	public void DoInvokesSameThreadShouldExecuteAllTasks()
	{
		Invoker invoker = new();
		bool invoked1 = false, invoked2 = false;

		Thread thread = new(() =>
		{
			_ = invoker.InvokeAsync(() => invoked1 = true);
			_ = invoker.InvokeAsync(() => invoked2 = true);
		});
		thread.Start();
		thread.Join();

		Assert.IsFalse(invoked1 && invoked2, "Tasks should not be executed yet");
		Assert.HasCount(2, invoker.TaskQueue, "Tasks should be queued.");

		invoker.DoInvokes();

		Assert.IsTrue(invoked1 && invoked2, "All queued tasks should be executed on same thread.");
	}

	[TestMethod]
	public void InvokeActionFromOtherThreadShouldPreserveStackTraceOfFailingCode()
	{
		Invoker invoker = new();
		Exception? caught = RunOnWorkerWhilePumping(invoker, () => invoker.Invoke(() => ThrowFromNamedHelper()));

		Assert.IsInstanceOfType<InvalidOperationException>(caught);
		Assert.Contains(nameof(ThrowFromNamedHelper), caught.StackTrace ?? string.Empty);
	}

	[TestMethod]
	public void InvokeFunctionFromOtherThreadShouldPreserveStackTraceOfFailingCode()
	{
		Invoker invoker = new();
		Exception? caught = RunOnWorkerWhilePumping(invoker, () => invoker.Invoke(ReturnFromNamedHelper));

		Assert.IsInstanceOfType<InvalidOperationException>(caught);
		Assert.Contains(nameof(ReturnFromNamedHelper), caught.StackTrace ?? string.Empty);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void ThrowFromNamedHelper() => throw new InvalidOperationException("failed on the owner thread");

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static int ReturnFromNamedHelper() => throw new InvalidOperationException("failed on the owner thread");

	/// <summary>
	/// Runs <paramref name="call"/> on a worker thread while the calling (owner) thread pumps
	/// <see cref="Invoker.DoInvokes"/>, and returns whatever the call threw.
	/// </summary>
	private static Exception? RunOnWorkerWhilePumping(Invoker invoker, Action call)
	{
		Exception? caught = null;
		Thread worker = new(() =>
		{
			try
			{
				call();
			}
			catch (InvalidOperationException e)
			{
				caught = e;
			}
		});
		worker.Start();

		while (!worker.Join(1))
		{
			invoker.DoInvokes();
		}

		return caught;
	}

	[TestMethod]
	public async Task InvokeAsyncActionContinuationDoesNotRunInsideDoInvokes()
	{
		Invoker invoker = new();
		await AssertContinuationLeavesOwnerThread(invoker, () => invoker.InvokeAsync(() => { })).ConfigureAwait(false);
	}

	[TestMethod]
	public async Task InvokeAsyncFunctionContinuationDoesNotRunInsideDoInvokes()
	{
		Invoker invoker = new();
		await AssertContinuationLeavesOwnerThread(invoker, () => invoker.InvokeAsync(() => 42)).ConfigureAwait(false);
	}

	/// <summary>
	/// Awaits <paramref name="invoke"/> from a thread-pool thread whose code after the await blocks
	/// until <see cref="Invoker.DoInvokes"/> has returned. If that code ran inline inside the pump, it
	/// would be waiting on itself, time out, and see that DoInvokes had not returned yet.
	/// </summary>
	/// <remarks>
	/// The test's own thread is a pool thread too, so once it stops pumping it may legitimately pick
	/// the continuation up. What must not happen is the continuation running on it during the pump.
	/// </remarks>
	private static async Task AssertContinuationLeavesOwnerThread(Invoker invoker, Func<Task> invoke)
	{
		int pumpingThreadId = 0;
		using ManualResetEventSlim doInvokesReturned = new();
		bool ranInsidePump = false;
		bool sawDoInvokesReturn = false;

		Task worker = Task.Run(async () =>
		{
			await invoke().ConfigureAwait(false);
			ranInsidePump = Volatile.Read(ref pumpingThreadId) == Environment.CurrentManagedThreadId;
			sawDoInvokesReturn = doInvokesReturned.Wait(TimeSpan.FromSeconds(2));
		});

		Assert.IsTrue(SpinWait.SpinUntil(() => !invoker.TaskQueue.IsEmpty, TimeSpan.FromSeconds(5)), "The worker should queue its delegate.");

		Volatile.Write(ref pumpingThreadId, Environment.CurrentManagedThreadId);
		invoker.DoInvokes();
		Volatile.Write(ref pumpingThreadId, 0);
		doInvokesReturned.Set();
		await worker.ConfigureAwait(false);

		Assert.IsFalse(ranInsidePump, "Code after the await should not run on the owner thread inside DoInvokes.");
		Assert.IsTrue(sawDoInvokesReturn, "DoInvokes should return without waiting on the caller's code after the await.");
	}
}
