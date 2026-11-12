// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.Invoker.Test;

using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// A delegate whose task faults with several exceptions, such as <see cref="Task.WhenAll(Task[])"/> over
/// several failures, hands every one of them to the task the invoker returns, as calling it directly would.
/// </summary>
[TestClass]
public class AggregateFaultTests
{
	private static Task FailBoth() => Task.WhenAll(
		Task.FromException(new InvalidOperationException("a")),
		Task.FromException(new FormatException("b")));

	private static Task<int[]> FailBothWithResult() => Task.WhenAll(
		Task.FromException<int>(new InvalidOperationException("a")),
		Task.FromException<int>(new FormatException("b")));

	[TestMethod]
	public async Task InvokeAsyncOnTheOwnerThreadKeepsEveryInnerException()
	{
		Invoker invoker = new();

		await AssertFaultsWithBoth(invoker.InvokeAsync(FailBoth)).ConfigureAwait(false);
	}

	[TestMethod]
	public async Task InvokeAsyncWithAResultOnTheOwnerThreadKeepsEveryInnerException()
	{
		Invoker invoker = new();

		await AssertFaultsWithBoth(invoker.InvokeAsync(FailBothWithResult)).ConfigureAwait(false);
	}

	[TestMethod]
	public async Task QueuedInvokeAsyncKeepsEveryInnerException()
	{
		Invoker invoker = new();

		Task call = Queue(invoker, () => invoker.InvokeAsync(FailBoth));

		await AssertFaultsWithBoth(call).ConfigureAwait(false);
	}

	[TestMethod]
	public async Task QueuedInvokeAsyncWithAResultKeepsEveryInnerException()
	{
		Invoker invoker = new();

		Task call = Queue(invoker, () => invoker.InvokeAsync(FailBothWithResult));

		await AssertFaultsWithBoth(call).ConfigureAwait(false);
	}

	[TestMethod]
	public async Task InvokeValueTaskAsyncKeepsEveryInnerException()
	{
		Invoker invoker = new();

		await AssertFaultsWithBoth(invoker.InvokeValueTaskAsync(() => new ValueTask(FailBoth()))).ConfigureAwait(false);
		await AssertFaultsWithBoth(invoker.InvokeValueTaskAsync(() => new ValueTask<int[]>(FailBothWithResult()))).ConfigureAwait(false);
	}

	[TestMethod]
	public async Task AwaitingTheCallStillThrowsTheFirstException()
	{
		Invoker invoker = new();

		InvalidOperationException thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => invoker.InvokeAsync(FailBoth)).ConfigureAwait(false);

		Assert.AreEqual("a", thrown.Message);
	}

	[TestMethod]
	public void InvokeStillThrowsTheFirstException()
	{
		Invoker invoker = new();

		InvalidOperationException thrown = Assert.ThrowsExactly<InvalidOperationException>(() => invoker.Invoke(FailBoth));

		Assert.AreEqual("a", thrown.Message);
	}

	/// <summary>
	/// Starts <paramref name="call"/> off the owner thread, then pumps its queued delegate on this one.
	/// </summary>
	private static Task Queue(Invoker invoker, Func<Task> call)
	{
		Task started = Task.Run(call);
		Assert.IsTrue(SpinWait.SpinUntil(() => !invoker.TaskQueue.IsEmpty, TimeSpan.FromSeconds(5)), "The call should queue its delegate.");
		invoker.DoInvokes();
		return started;
	}

	private static async Task AssertFaultsWithBoth(Task call)
	{
		Task finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
		Assert.AreSame(call, finished, "The call should complete.");

		Assert.IsTrue(call.IsFaulted, $"Expected a faulted task, got {call.Status}.");
		Assert.HasCount(2, call.Exception!.InnerExceptions, string.Join("\n", call.Exception.InnerExceptions));
		Assert.IsInstanceOfType<InvalidOperationException>(call.Exception.InnerExceptions[0]);
		Assert.IsInstanceOfType<FormatException>(call.Exception.InnerExceptions[1]);
	}
}
