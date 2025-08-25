// Copyright (c) ktsu.dev
// All rights reserved.
// Licensed under the MIT license.

namespace ktsu.Invoker.Test;

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

		Task.Run(() =>
		{
			_ = invoker.InvokeAsync(() => invoked1 = true);
			_ = invoker.InvokeAsync(() => invoked2 = true);
		}).Wait();

		Assert.IsFalse(invoked1 && invoked2, "Tasks should not be executed yet");
		Assert.AreEqual(2, invoker.TaskQueue.Count, "Tasks should be queued.");

		invoker.DoInvokes();

		Assert.IsTrue(invoked1 && invoked2, "All queued tasks should be executed on same thread.");
	}
}
