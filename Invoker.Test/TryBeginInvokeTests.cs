// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.Invoker.Test;

using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class TryBeginInvokeTests
{
	public TestContext TestContext { get; set; } = null!;

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
	[DataRow((1 << 30) + 1)]
	[DataRow(int.MaxValue)]
	public void CapacityAboveTheMaximumShouldThrowArgumentOutOfRangeException(int capacity)
	{
		// Rounding these up to a power of two overflows an int, which surfaced as an
		// OverflowException from the array allocation instead of the documented argument exception.
		ArgumentOutOfRangeException exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Invoker(capacity));
		Assert.AreEqual(capacity, exception.ActualValue);
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

	[TestMethod]
	public void ThrowingActionDoesNotStarveQueuedInvoke()
	{
		Invoker invoker = new();
		List<Exception> failures = [];
		invoker.BeginInvokeFailed += (_, e) => failures.Add(e.Exception);

		Thread producer = new(() => invoker.TryBeginInvoke(() => throw new InvalidOperationException("fire-and-forget failed")));
		producer.Start();
		producer.Join();

		bool invokeRan = false;
		Task worker = Task.Run(() => invoker.Invoke(() => invokeRan = true));
		SpinWait.SpinUntil(() => !invoker.TaskQueue.IsEmpty, TimeSpan.FromSeconds(5));

		invoker.DoInvokes();

		Assert.IsTrue(invokeRan, "The queued Invoke should run in the same pump as the throwing action.");
		Assert.IsTrue(worker.Wait(TimeSpan.FromSeconds(5), TestContext.CancellationToken), "The thread blocked in Invoke should be released.");
		Assert.HasCount(1, failures);
		Assert.AreEqual("fire-and-forget failed", failures[0].Message);
	}

	[TestMethod]
	public void ThrowingActionDoesNotSkipLaterQueuedActions()
	{
		Invoker invoker = new();
		invoker.BeginInvokeFailed += (_, _) => { };
		bool laterRan = false;

		Thread producer = new(() =>
		{
			invoker.TryBeginInvoke(() => throw new InvalidOperationException("first"));
			invoker.TryBeginInvoke(() => laterRan = true);
		});
		producer.Start();
		producer.Join();

		invoker.DoInvokes();

		Assert.IsTrue(laterRan, "Actions queued after a throwing one should still run in the same pump.");
	}

	[TestMethod]
	public void ThrowingActionWithoutHandlerThrowsAggregateAfterDraining()
	{
		Invoker invoker = new();

		Thread producer = new(() =>
		{
			invoker.TryBeginInvoke(() => throw new InvalidOperationException("first"));
			invoker.TryBeginInvoke(() => throw new ArgumentException("second"));
		});
		producer.Start();
		producer.Join();

		bool invokeRan = false;
		Task worker = Task.Run(() => invoker.Invoke(() => invokeRan = true));
		SpinWait.SpinUntil(() => !invoker.TaskQueue.IsEmpty, TimeSpan.FromSeconds(5));

		AggregateException aggregate = Assert.ThrowsExactly<AggregateException>(invoker.DoInvokes);

		Assert.HasCount(2, aggregate.InnerExceptions);
		Assert.IsInstanceOfType<InvalidOperationException>(aggregate.InnerExceptions[0]);
		Assert.IsInstanceOfType<ArgumentException>(aggregate.InnerExceptions[1]);
		Assert.IsTrue(invokeRan, "Both queues should be drained before the aggregate is thrown.");
		Assert.IsTrue(worker.Wait(TimeSpan.FromSeconds(5), TestContext.CancellationToken), "The thread blocked in Invoke should be released.");
	}

	[TestMethod]
	public void InvokeAsyncThenTryBeginInvokeFromOneThreadRunInSubmissionOrder()
	{
		Invoker invoker = new();
		List<string> order = [];
		Task? first = null;

		Thread producer = new(() =>
		{
			first = invoker.InvokeAsync(() => order.Add("A"));
			invoker.TryBeginInvoke(() => order.Add("B"));
		});
		producer.Start();
		producer.Join();

		invoker.DoInvokes();

		Assert.IsTrue(first!.Wait(TimeSpan.FromSeconds(5), TestContext.CancellationToken), "The InvokeAsync task should complete.");
		Assert.AreEqual("A B", string.Join(' ', order), "Work from one thread should run in the order it was submitted, across both APIs.");
	}

	[TestMethod]
	public void InterleavedSubmissionsFromOneThreadRunInSubmissionOrder()
	{
		Invoker invoker = new();
		List<string> order = [];
		List<Task> tasks = [];

		Thread producer = new(() =>
		{
			invoker.TryBeginInvoke(() => order.Add("B1"));
			tasks.Add(invoker.InvokeAsync(() => order.Add("A1")));
			tasks.Add(invoker.InvokeAsync(() => order.Add("A2")));
			invoker.TryBeginInvoke(() => order.Add("B2"));
			invoker.TryBeginInvoke(() => order.Add("B3"));
			tasks.Add(invoker.InvokeAsync(() => order.Add("A3")));
		});
		producer.Start();
		producer.Join();

		invoker.DoInvokes();

		Assert.IsTrue(Task.WaitAll([.. tasks], TimeSpan.FromSeconds(5)), "Every InvokeAsync task should complete.");
		Assert.AreEqual("B1 A1 A2 B2 B3 A3", string.Join(' ', order));
	}

	[TestMethod]
	public void DoInvokesReturnsWhileProducerKeepsPosting()
	{
		const int capacity = 64;
		Invoker invoker = new(capacity);
		using ProducerFlood flood = new(invoker);

		bool invokeRan = false;
		Task worker = Task.Run(() => invoker.Invoke(() => invokeRan = true));
		SpinWait.SpinUntil(() => !invoker.TaskQueue.IsEmpty, TimeSpan.FromSeconds(5));

		invoker.DoInvokes();
		bool producerStillRunning = !flood.WatchdogFired;

		Assert.IsTrue(producerStillRunning, "DoInvokes should return while the producer is still posting, not only once it stops.");
		Assert.IsLessThanOrEqualTo(capacity, flood.Ran, "One pump should run no more actions than were queued when it started.");
		Assert.IsTrue(invokeRan, "An Invoke pending when the pump started should run in that same pump.");
		Assert.IsTrue(worker.Wait(TimeSpan.FromSeconds(5), TestContext.CancellationToken), "The thread blocked in Invoke should be released.");
	}

	[TestMethod]
	public void ActionQueuedDuringDoInvokesRunsOnNextPump()
	{
		Invoker invoker = new();
		using ManualResetEventSlim queuedDuringPump = new();
		bool laterRan = false;

		Thread producer = new(() => invoker.TryBeginInvoke(() =>
		{
			Thread poster = new(() => invoker.TryBeginInvoke(() => laterRan = true));
			poster.Start();
			poster.Join();
			queuedDuringPump.Set();
		}));
		producer.Start();
		producer.Join();

		invoker.DoInvokes();
		Assert.IsTrue(queuedDuringPump.IsSet, "The first action should have run.");
		Assert.IsFalse(laterRan, "An action queued while the pump is running should wait for the next pump.");

		invoker.DoInvokes();
		Assert.IsTrue(laterRan, "The action queued during the previous pump should run on this one.");
	}

	/// <summary>
	/// A producer thread that posts slow actions with <see cref="Invoker.TryBeginInvoke"/> as fast as it
	/// can, so the queue refills as quickly as the owner thread drains it. A watchdog stops it after a few
	/// seconds, so a pump that never returns on its own fails the test instead of hanging it.
	/// </summary>
	private sealed class ProducerFlood : IDisposable
	{
		private readonly Thread producer;
		private readonly Timer watchdog;
		private readonly ManualResetEventSlim filled = new();
		private int ran;
		private volatile bool stop;
		private volatile bool watchdogFired;

		public ProducerFlood(Invoker invoker)
		{
			producer = new Thread(() =>
			{
				while (!stop)
				{
					bool queued = invoker.TryBeginInvoke(() =>
					{
						Interlocked.Increment(ref ran);
						Thread.SpinWait(5000);
					});

					if (!queued)
					{
						filled.Set();
					}
				}
			})
			{ IsBackground = true };
			producer.Start();
			filled.Wait(TimeSpan.FromSeconds(5));
			watchdog = new Timer(_ =>
			{
				watchdogFired = true;
				stop = true;
			}, null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
		}

		public int Ran => Volatile.Read(ref ran);

		public bool WatchdogFired => watchdogFired;

		public void Dispose()
		{
			stop = true;
			watchdog.Dispose();
			producer.Join();
			filled.Dispose();
		}
	}
}
