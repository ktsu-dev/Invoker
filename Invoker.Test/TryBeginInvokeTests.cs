// Copyright (c) ktsu.dev
// All rights reserved.
// Licensed under the MIT license.

namespace ktsu.Invoker.Test;

using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class TryBeginInvokeTests
{
	[TestMethod]
	public void TryBeginInvokeNullShouldThrowArgumentNullException()
	{
		Invoker invoker = new();
		Assert.ThrowsExactly<ArgumentNullException>(() => invoker.TryBeginInvoke(null!));
	}

	[TestMethod]
	public void TryBeginInvokeSameThreadShouldInvokeImmediately()
	{
		Invoker invoker = new();
		bool invoked = false;
		bool result = invoker.TryBeginInvoke(() => invoked = true);
		Assert.IsTrue(result, "Should report success when invoked on the owner thread.");
		Assert.IsTrue(invoked, "Action should be invoked immediately on the owner thread.");
	}

	[TestMethod]
	public void TryBeginInvokeOtherThreadShouldQueueUntilDoInvokes()
	{
		Invoker invoker = new();
		bool invoked = false;

		Thread thread = new(() => invoker.TryBeginInvoke(() => invoked = true));
		thread.Start();
		thread.Join();

		Assert.IsFalse(invoked, "Action should not run before DoInvokes is pumped.");
		invoker.DoInvokes();
		Assert.IsTrue(invoked, "Action should run when DoInvokes is pumped on the owner thread.");
	}

	[TestMethod]
	public void TryBeginInvokePreservesFifoOrder()
	{
		Invoker invoker = new();
		List<int> order = [];

		Thread thread = new(() =>
		{
			for (int i = 0; i < 50; i++)
			{
				int captured = i;
				invoker.TryBeginInvoke(() => order.Add(captured));
			}
		});
		thread.Start();
		thread.Join();

		invoker.DoInvokes();

		Assert.HasCount(50, order);
		for (int i = 0; i < 50; i++)
		{
			Assert.AreEqual(i, order[i], "Actions should execute in the order they were queued.");
		}
	}

	[TestMethod]
	public void TryBeginInvokeReturnsFalseWhenQueueFull()
	{
		// Small capacity (rounded up to a power of two) so we can fill it from a non-owner thread.
		Invoker invoker = new(2);
		bool sawFull = false;
		int accepted = 0;

		Thread thread = new(() =>
		{
			for (int i = 0; i < 100; i++)
			{
				if (invoker.TryBeginInvoke(() => { }))
				{
					accepted++;
				}
				else
				{
					sawFull = true;
				}
			}
		});
		thread.Start();
		thread.Join();

		Assert.IsTrue(sawFull, "Queue should report full once capacity is exceeded without pumping.");
		Assert.IsTrue(accepted > 0, "Some actions should have been accepted before the queue filled.");
	}

	[TestMethod]
	public void TryBeginInvokeConcurrentProducersDeliverAllActions()
	{
		const int producers = 4;
		const int perProducer = 10_000;
		const int total = producers * perProducer;

		Invoker invoker = new(total);
		int executed = 0;

		Thread[] threads = new Thread[producers];
		for (int p = 0; p < producers; p++)
		{
			threads[p] = new Thread(() =>
			{
				for (int i = 0; i < perProducer; i++)
				{
					while (!invoker.TryBeginInvoke(() => Interlocked.Increment(ref executed)))
					{
						Thread.SpinWait(1);
					}
				}
			});
		}

		foreach (Thread thread in threads)
		{
			thread.Start();
		}

		foreach (Thread thread in threads)
		{
			thread.Join();
		}

		invoker.DoInvokes();

		Assert.AreEqual(total, executed, "Every queued action from every producer must run exactly once.");
	}
}
