// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.Invoker.Test;

using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// A caller blocked on queued work can be released once the owner loop stops pumping (#63).
/// </summary>
[TestClass]
public sealed class CancellationAndShutdownTests
{
	private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

	/// <summary>
	/// Runs <paramref name="func"/> on a new thread and waits for it. A blocking wait on <c>Task.Run</c>
	/// could inline the task onto this, the owner, thread, and the call would then run inline.
	/// </summary>
	private static T OnAnotherThread<T>(Func<T> func)
	{
		T result = default!;
		Thread thread = new(() => result = func())
		{
			IsBackground = true,
		};
		thread.Start();
		Assert.IsTrue(thread.Join(Bound), "The other thread should finish.");
		return result;
	}

	[TestMethod]
	public void CancellingTheTokenReleasesABlockedInvokeAndTheActionNeverRuns()
	{
		Invoker invoker = new();
		using CancellationTokenSource cancellation = new();
		int ran = 0;
		OperationCanceledException? caught = null;

		Thread worker = new(() =>
		{
			try
			{
				invoker.Invoke(() => Interlocked.Increment(ref ran), cancellation.Token);
			}
			catch (OperationCanceledException ex)
			{
				caught = ex;
			}
		})
		{
			IsBackground = true,
		};
		worker.Start();
		Assert.IsTrue(SpinWait.SpinUntil(() => !invoker.TaskQueue.IsEmpty, Bound), "The worker should queue its action.");

		cancellation.Cancel();

		Assert.IsTrue(worker.Join(Bound), "Cancelling the token should release the blocked caller.");
		Assert.IsNotNull(caught, "The caller should see an OperationCanceledException.");
		Assert.AreEqual(cancellation.Token, caught.CancellationToken, "The caller should see its own token.");

		invoker.DoInvokes();
		Assert.AreEqual(0, ran, "A withdrawn action must not run when the owner pumps again.");
	}

	[TestMethod]
	public void CancellingTheTokenCancelsAQueuedInvokeAsyncFunction()
	{
		Invoker invoker = new();
		using CancellationTokenSource cancellation = new();
		int ran = 0;

		Task<int> call = Task.Run(() => invoker.InvokeAsync(() => Interlocked.Increment(ref ran), cancellation.Token));
		Assert.IsTrue(SpinWait.SpinUntil(() => !invoker.TaskQueue.IsEmpty, Bound), "The call should queue its function.");

		// Every step stays on this, the owner, thread: an await here could resume on another one.
		cancellation.Cancel();

		Assert.IsTrue(SpinWait.SpinUntil(() => call.IsCompleted, Bound), "Cancelling the token should complete the caller's task.");
		Assert.IsTrue(call.IsCanceled, "The caller's task should be cancelled.");

		invoker.DoInvokes();
		Assert.AreEqual(0, ran);
	}

	[TestMethod]
	public async Task AnAsyncLambdaWithATokenIsAwaitedToItsEnd()
	{
		Invoker invoker = new();
		TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		bool finishedBody = false;

		Task call = Task.Run(() => invoker.InvokeAsync(
			async () =>
			{
				await gate.Task.ConfigureAwait(false);
				finishedBody = true;
			},
			CancellationToken.None));
		Assert.IsTrue(SpinWait.SpinUntil(() => !invoker.TaskQueue.IsEmpty, Bound));

		invoker.DoInvokes();
		Assert.IsFalse(call.IsCompleted, "The call should wait for the lambda's own task, not its first await.");

		gate.SetResult();
		await call.WaitAsync(Bound).ConfigureAwait(false);
		Assert.IsTrue(finishedBody);
	}

	[TestMethod]
	public void ATokenCancelledAfterTheActionRanChangesNothing()
	{
		Invoker invoker = new();
		using CancellationTokenSource cancellation = new();
		int result = 0;

		Thread worker = new(() => result = invoker.Invoke(() => 42, cancellation.Token))
		{
			IsBackground = true,
		};
		worker.Start();
		Assert.IsTrue(SpinWait.SpinUntil(() => !invoker.TaskQueue.IsEmpty, Bound));

		invoker.DoInvokes();
		Assert.IsTrue(worker.Join(Bound));
		cancellation.Cancel();

		Assert.AreEqual(42, result);
	}

	[TestMethod]
	public void OnTheOwnerThreadACancelledTokenThrowsAndAnUncancelledOneRunsInline()
	{
		Invoker invoker = new();
		using CancellationTokenSource cancellation = new();
		int ran = 0;

		invoker.Invoke(() => ran++, cancellation.Token);
		Assert.AreEqual(1, ran);

		cancellation.Cancel();
		Assert.ThrowsExactly<OperationCanceledException>(() => invoker.Invoke(() => ran++, cancellation.Token));
		Assert.AreEqual(1, ran);
	}

	[TestMethod]
	public void ShutdownReleasesTheWorkerFromTheIssueRepro()
	{
		// The repro from #63: the owner loop ends, then the worker is signalled and joined.
		Invoker invoker = new();
		using CancellationTokenSource stop = new();
		int ranAfterShutdown = 0;
		bool shutDown = false;
		Exception? caught = null;

		Thread worker = new(() =>
		{
			try
			{
				while (!stop.IsCancellationRequested)
				{
					invoker.Invoke(() =>
					{
						if (Volatile.Read(ref shutDown))
						{
							Interlocked.Increment(ref ranAfterShutdown);
						}
					});
				}
			}
			catch (OperationCanceledException ex)
			{
				caught = ex;
			}
		})
		{
			IsBackground = true,
		};
		worker.Start();

		for (int frame = 0; frame < 5; frame++)
		{
			invoker.DoInvokes();
			Thread.Sleep(10);
		}

		Volatile.Write(ref shutDown, true);
		invoker.Shutdown();
		stop.Cancel();

		Assert.IsTrue(worker.Join(Bound), "Shutdown should release the worker blocked in Invoke.");
		Assert.IsInstanceOfType<OperationCanceledException>(caught);

		invoker.DoInvokes();
		Assert.AreEqual(0, ranAfterShutdown, "No delegate may run after Shutdown.");
	}

	[TestMethod]
	public void ShutdownCancelsEveryQueuedCallAndDropsQueuedActions()
	{
		Invoker invoker = new();
		int ran = 0;

		Task first = Task.Run(() => invoker.InvokeAsync(() => Interlocked.Increment(ref ran)));
		Task<int> second = Task.Run(() => invoker.InvokeAsync(() => Interlocked.Increment(ref ran)));
		Task third = Task.Run(() => invoker.InvokeAsync(async () => await Task.Yield()));
		Assert.IsTrue(SpinWait.SpinUntil(() => invoker.TaskQueue.Count == 3, Bound));
		Assert.IsTrue(OnAnotherThread(() => invoker.TryBeginInvoke(() => Interlocked.Increment(ref ran))));

		invoker.Shutdown();

		foreach (Task call in new[] { first, second, third })
		{
			Assert.IsTrue(SpinWait.SpinUntil(() => call.IsCompleted, Bound), "Shutdown should complete every queued call.");
			Assert.IsTrue(call.IsCanceled, "Every queued call should be cancelled.");
		}

		invoker.DoInvokes();
		Assert.AreEqual(0, ran);
		Assert.IsTrue(invoker.IsShutDown);
	}

	[TestMethod]
	public void AfterShutdownCrossThreadCallsFailFastAndTryBeginInvokeReturnsFalse()
	{
		Invoker invoker = new();
		invoker.Shutdown();
		int ran = 0;

		OperationCanceledException? caught = OnAnotherThread(() =>
		{
			try
			{
				invoker.Invoke(() => ran++);
				return null;
			}
			catch (OperationCanceledException ex)
			{
				return ex;
			}
		});

		Assert.IsNotNull(caught, "Invoke from another thread should fail at once after Shutdown.");
		Assert.IsFalse(OnAnotherThread(() => invoker.TryBeginInvoke(() => ran++)));
		Assert.IsTrue(invoker.TaskQueue.IsEmpty, "Nothing should be queued after Shutdown.");

		invoker.DoInvokes();
		Assert.AreEqual(0, ran);

		// The owner thread still runs its own calls inline.
		invoker.Invoke(() => ran++);
		Assert.AreEqual(1, ran);
	}

	[TestMethod]
	public void ShutdownFromAnotherThreadThrows()
	{
		Invoker invoker = new();

		// A dedicated thread: once an async test awaits, Task.Run could land on the owner's own pool thread.
		Exception? caught = OnAnotherThread<Exception?>(() =>
		{
			try
			{
				invoker.Shutdown();
				return null;
			}
			catch (InvalidOperationException ex)
			{
				return ex;
			}
		});

		Assert.IsNotNull(caught);
		Assert.IsFalse(invoker.IsShutDown);
	}
}
