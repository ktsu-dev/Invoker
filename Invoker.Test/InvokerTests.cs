// Copyright (c) 2023-2026 ktsu-dev contributors

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace ktsu.Invoker.Test;

using System.Diagnostics;
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
	public async Task DoInvokesFromATaskOnANonInliningSchedulerRunsInvokeOnTheOwnerThread()
	{
		NonInliningTaskScheduler scheduler = new();

		// The owner loop is itself a task on a scheduler that refuses to inline, so inside it
		// TaskScheduler.Current is that scheduler rather than the default one.
		Task<(int OwnerThread, int InvokedThread)> ownerLoop = Task.Factory.StartNew(
			() =>
			{
				Invoker invoker = new();
				Task<int> caller = Task.Run(() => invoker.Invoke(() => Environment.CurrentManagedThreadId));

				SpinWait.SpinUntil(() => !invoker.TaskQueue.IsEmpty, TimeSpan.FromSeconds(10));
				invoker.DoInvokes();

				return (Environment.CurrentManagedThreadId, caller.GetAwaiter().GetResult());
			},
			CancellationToken.None,
			TaskCreationOptions.None,
			scheduler);

		Task finished = await Task.WhenAny(ownerLoop, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
		Assert.AreSame(ownerLoop, finished, "Expected DoInvokes to return.");

		(int ownerThread, int invokedThread) = await ownerLoop.ConfigureAwait(false);
		Assert.AreEqual(ownerThread, invokedThread, "Expected the Invoke delegate to run on the owner thread.");
	}

	/// <summary>
	/// Runs each task on the thread pool and never inlines one, like a custom game-loop or actor
	/// scheduler might.
	/// </summary>
	private sealed class NonInliningTaskScheduler : TaskScheduler
	{
		protected override IEnumerable<Task>? GetScheduledTasks() => null;

		protected override void QueueTask(Task task) =>
			ThreadPool.UnsafeQueueUserWorkItem(_ => TryExecuteTask(task), null);

		protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
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
		Assert.ThrowsExactly<ArgumentNullException>(() => invoker.Invoke((Func<int>)null!));
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

	[TestMethod]
	public async Task InvokeAsyncTaskDelegateContinuationDoesNotRunOnTheOwnerThread()
	{
		Invoker invoker = new();
		await AssertContinuationLeavesOwnerThreadThatCompletesTheDelegate(invoker, signal => invoker.InvokeAsync(async () => await signal.ConfigureAwait(false))).ConfigureAwait(false);
	}

	[TestMethod]
	public async Task InvokeAsyncTaskOfTDelegateContinuationDoesNotRunOnTheOwnerThread()
	{
		Invoker invoker = new();
		await AssertContinuationLeavesOwnerThreadThatCompletesTheDelegate(invoker, signal => invoker.InvokeAsync(async () =>
		{
			await signal.ConfigureAwait(false);
			return 42;
		})).ConfigureAwait(false);
	}

	[TestMethod]
	public async Task InvokeAsyncValueTaskDelegateContinuationDoesNotRunOnTheOwnerThread()
	{
		Invoker invoker = new();
		await AssertContinuationLeavesOwnerThreadThatCompletesTheDelegate(invoker, signal => invoker.InvokeAsync(() => new ValueTask(signal))).ConfigureAwait(false);
	}

	[TestMethod]
	public async Task InvokeAsyncValueTaskOfTDelegateContinuationDoesNotRunOnTheOwnerThread()
	{
		Invoker invoker = new();
		await AssertContinuationLeavesOwnerThreadThatCompletesTheDelegate(invoker, signal => invoker.InvokeAsync(() => new ValueTask<int>(ReturnAfter(signal)))).ConfigureAwait(false);

		static async Task<int> ReturnAfter(Task signal)
		{
			await signal.ConfigureAwait(false);
			return 42;
		}
	}

	[TestMethod]
	public async Task InvokeAsyncTaskDelegateThatIsCanceledCancelsTheCallersTaskWithItsToken()
	{
		Invoker invoker = new();
		using CancellationTokenSource cts = new();
		await cts.CancelAsync().ConfigureAwait(false);

		Task task = invoker.InvokeAsync(async () =>
		{
			await Task.Yield();
			cts.Token.ThrowIfCancellationRequested();
		});

		OperationCanceledException e = await Assert.ThrowsAsync<OperationCanceledException>(() => task).ConfigureAwait(false);
		Assert.IsTrue(task.IsCanceled, "The caller's task should be canceled, not faulted.");
		Assert.AreEqual(cts.Token, e.CancellationToken);
	}

	[TestMethod]
	public async Task InvokeAsyncTaskOfTDelegateThatIsCanceledCancelsTheCallersTaskWithItsToken()
	{
		Invoker invoker = new();
		using CancellationTokenSource cts = new();
		await cts.CancelAsync().ConfigureAwait(false);

		Task<int> task = invoker.InvokeAsync(async () =>
		{
			await Task.Yield();
			cts.Token.ThrowIfCancellationRequested();
			return 42;
		});

		OperationCanceledException e = await Assert.ThrowsAsync<OperationCanceledException>(() => task).ConfigureAwait(false);
		Assert.IsTrue(task.IsCanceled, "The caller's task should be canceled, not faulted.");
		Assert.AreEqual(cts.Token, e.CancellationToken);
	}

	[TestMethod]
	public async Task InvokeAsyncTaskOfTDelegateThatThrowsAfterAnAwaitFaultsTheCallersTask()
	{
		Invoker invoker = new();

		Task<int> task = invoker.InvokeAsync<int>(async () =>
		{
			await Task.Yield();
			throw new InvalidOperationException("after await");
		});

		InvalidOperationException e = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => task).ConfigureAwait(false);
		Assert.AreEqual("after await", e.Message);
		Assert.IsTrue(task.IsFaulted);
	}

	/// <summary>
	/// Like <see cref="AssertContinuationLeavesOwnerThread"/>, for a delegate whose task the owner thread
	/// itself completes after the pump, the way a render loop completes a "next frame" source (#102).
	/// The source completes its continuations inline, so without a hop the caller's code after its
	/// await would run inside <see cref="TaskCompletionSource.SetResult()"/> on the owner thread.
	/// </summary>
	private static async Task AssertContinuationLeavesOwnerThreadThatCompletesTheDelegate(Invoker invoker, Func<Task, Task> invoke)
	{
		TaskCompletionSource signal = new();
		int ownerThreadId = 0;
		using ManualResetEventSlim setResultReturned = new();
		bool ranOnOwner = false;
		bool sawSetResultReturn = false;

		Task worker = Task.Run(async () =>
		{
			await invoke(signal.Task).ConfigureAwait(false);
			ranOnOwner = Volatile.Read(ref ownerThreadId) == Environment.CurrentManagedThreadId;
			sawSetResultReturn = setResultReturned.Wait(TimeSpan.FromSeconds(2));
		});

		Assert.IsTrue(SpinWait.SpinUntil(() => !invoker.TaskQueue.IsEmpty, TimeSpan.FromSeconds(5)), "The worker should queue its delegate.");

		Volatile.Write(ref ownerThreadId, Environment.CurrentManagedThreadId);
		invoker.DoInvokes();

		// Give the caller's side time to start waiting on the delegate's task. Completing it before then
		// would leave nothing to run inline, and the test would pass whether or not the hop is there.
		Thread.Sleep(TimeSpan.FromMilliseconds(250));
		signal.SetResult();
		Volatile.Write(ref ownerThreadId, 0);
		setResultReturned.Set();
		await worker.ConfigureAwait(false);

		Assert.IsFalse(ranOnOwner, "Code after the await should not run on the owner thread that completed the delegate's task.");
		Assert.IsTrue(sawSetResultReturn, "Completing the delegate's task should not wait on the caller's code after the await.");
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

	[TestMethod]
	public void InvokeAsyncAsyncLambdaWaitsForCompletion()
	{
		Invoker invoker = new();
		Stopwatch elapsed = Stopwatch.StartNew();
		long completedAfter = 0;

		Exception? caught = RunOnWorkerWhilePumping(invoker, () =>
		{
			invoker.InvokeAsync(async () => await Task.Delay(200).ConfigureAwait(false)).GetAwaiter().GetResult();
			completedAfter = elapsed.ElapsedMilliseconds;
		});

		Assert.IsNull(caught);
		Assert.IsGreaterThanOrEqualTo(190, completedAfter, "InvokeAsync should not complete before the async lambda has finished.");
	}

	[TestMethod]
	public void InvokeAsyncAsyncLambdaPropagatesExceptionAfterFirstAwait()
	{
		Invoker invoker = new();

		Exception? caught = RunOnWorkerWhilePumping(invoker, () => invoker.InvokeAsync(async () =>
		{
			await Task.Yield();
			throw new InvalidOperationException("boom");
		}).GetAwaiter().GetResult());

		Assert.IsNotNull(caught, "An exception thrown after the lambda's first await should reach the caller.");
		Assert.AreEqual("boom", caught.Message);
	}

	[TestMethod]
	public void InvokeAsyncAsyncLambdaReturnsInnerResult()
	{
		Invoker invoker = new();
		int result = 0;

		Exception? caught = RunOnWorkerWhilePumping(invoker, () => result = invoker.InvokeAsync(async () =>
		{
			await Task.Yield();
			return 42;
		}).GetAwaiter().GetResult());

		Assert.IsNull(caught);
		Assert.AreEqual(42, result);
	}

	[TestMethod]
	public void InvokeAsyncAsyncLambdaOnOwnerThreadWaitsAndPropagates()
	{
		// Every call below runs on the thread that created the invoker, so each takes the owner-thread
		// fast path. Blocking here is safe because nothing in the lambdas needs DoInvokes to be pumped.
		Invoker invoker = new();
		bool finished = false;

		invoker.InvokeAsync(async () =>
		{
			await Task.Delay(50).ConfigureAwait(false);
			finished = true;
		}).GetAwaiter().GetResult();
		Assert.IsTrue(finished, "On the owner thread, InvokeAsync should still wait for the async lambda to finish.");

		int result = invoker.InvokeAsync(async () =>
		{
			await Task.Yield();
			return 42;
		}).GetAwaiter().GetResult();
		Assert.AreEqual(42, result);

		InvalidOperationException e = Assert.ThrowsExactly<InvalidOperationException>(() => invoker.InvokeAsync(async () =>
		{
			await Task.Yield();
			throw new InvalidOperationException("boom");
		}).GetAwaiter().GetResult());
		Assert.AreEqual("boom", e.Message);
	}

	[TestMethod]
	public void InvokeAsyncLambdaBlocksUntilCompletionAndPropagates()
	{
		Invoker invoker = new();
		bool finished = false;
		int result = 0;

		Exception? caught = RunOnWorkerWhilePumping(invoker, () =>
		{
			invoker.Invoke(async () =>
			{
				await Task.Delay(50).ConfigureAwait(false);
				finished = true;
			});
			result = invoker.Invoke(async () =>
			{
				await Task.Yield();
				return 42;
			});
			invoker.Invoke(async () =>
			{
				await Task.Yield();
				throw new InvalidOperationException("boom");
			});
		});

		Assert.IsTrue(finished, "Invoke should block until the async lambda has finished.");
		Assert.AreEqual(42, result);
		Assert.IsNotNull(caught, "Invoke should rethrow an exception thrown after the lambda's first await.");
		Assert.AreEqual("boom", caught.Message);
	}

	[TestMethod]
	public async Task InvokeAsyncAsyncFunctionNullShouldThrowArgumentNullException()
	{
		Invoker invoker = new();
		Func<Task> asyncAction = null!;
		Func<Task<int>> asyncFunction = null!;
		await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => invoker.InvokeAsync(asyncAction)).ConfigureAwait(false);
		await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => invoker.InvokeAsync(asyncFunction)).ConfigureAwait(false);
	}

	[TestMethod]
	public void InvokeAsyncNullThrowsAtTheCallSiteWithoutAwaiting()
	{
		// Fire-and-forget callers never await, so a null must throw from the call itself, not fault the task.
		Invoker invoker = new();
		Action action = null!;
		Func<int> function = null!;
		Func<Task> asyncAction = null!;
		Func<Task<int>> asyncFunction = null!;
		Assert.ThrowsExactly<ArgumentNullException>(() => _ = invoker.InvokeAsync(action));
		Assert.ThrowsExactly<ArgumentNullException>(() => _ = invoker.InvokeAsync(function));
		Assert.ThrowsExactly<ArgumentNullException>(() => _ = invoker.InvokeAsync(asyncAction));
		Assert.ThrowsExactly<ArgumentNullException>(() => _ = invoker.InvokeAsync(asyncFunction));
	}
	private static async ValueTask DelayThenSetAsync(StrongBox<bool> finished)
	{
		await Task.Delay(200).ConfigureAwait(false);
		finished.Value = true;
	}

	private static async ValueTask YieldThenThrowAsync()
	{
		await Task.Yield();
		throw new InvalidOperationException("boom");
	}

	private static async ValueTask<int> YieldThenReturnAsync()
	{
		await Task.Yield();
		return 42;
	}

	private static async ValueTask<int> YieldThenThrowWithResultAsync()
	{
		await Task.Yield();
		throw new InvalidOperationException("boom");
	}

	[TestMethod]
	public void InvokeAsyncValueTaskWaitsForCompletionAndPropagates()
	{
		Invoker invoker = new();
		StrongBox<bool> finished = new();
		Stopwatch elapsed = Stopwatch.StartNew();
		long completedAfter = 0;
		bool finishedWhenReturned = false;

		Exception? caught = RunOnWorkerWhilePumping(invoker, () =>
		{
			invoker.InvokeValueTaskAsync(() => DelayThenSetAsync(finished)).GetAwaiter().GetResult();
			completedAfter = elapsed.ElapsedMilliseconds;
			finishedWhenReturned = finished.Value;
			invoker.InvokeValueTaskAsync(YieldThenThrowAsync).GetAwaiter().GetResult();
		});

		Assert.IsTrue(finishedWhenReturned, "InvokeAsync should not complete before the ValueTask has finished.");
		Assert.IsGreaterThanOrEqualTo(190, completedAfter, "InvokeAsync should wait for the ValueTask to complete.");
		Assert.IsNotNull(caught, "An exception thrown after the ValueTask's first await should reach the caller.");
		Assert.AreEqual("boom", caught.Message);
	}

	[TestMethod]
	public void InvokeAsyncValueTaskOfTReturnsInnerResultAndPropagates()
	{
		Invoker invoker = new();
		int result = 0;

		Exception? caught = RunOnWorkerWhilePumping(invoker, () =>
		{
			result = invoker.InvokeValueTaskAsync(YieldThenReturnAsync).GetAwaiter().GetResult();
			_ = invoker.InvokeValueTaskAsync(YieldThenThrowWithResultAsync).GetAwaiter().GetResult();
		});

		Assert.AreEqual(42, result);
		Assert.IsNotNull(caught, "An exception thrown after the ValueTask's first await should reach the caller.");
		Assert.AreEqual("boom", caught.Message);
	}

	[TestMethod]
	public void InvokeValueTaskBlocksUntilCompletionAndPropagates()
	{
		Invoker invoker = new();
		StrongBox<bool> finished = new();
		int result = 0;

		Exception? caught = RunOnWorkerWhilePumping(invoker, () =>
		{
			invoker.InvokeValueTask(() => DelayThenSetAsync(finished));
			result = invoker.InvokeValueTask(YieldThenReturnAsync);
			invoker.InvokeValueTask(YieldThenThrowAsync);
		});

		Assert.IsTrue(finished.Value, "Invoke should block until the ValueTask has finished.");
		Assert.AreEqual(42, result);
		Assert.IsNotNull(caught, "Invoke should rethrow an exception thrown after the ValueTask's first await.");
		Assert.AreEqual("boom", caught.Message);
	}

	[TestMethod]
	public void InvokeValueTaskOfTRethrowsAfterFirstAwait()
	{
		Invoker invoker = new();

		Exception? caught = RunOnWorkerWhilePumping(invoker, () => _ = invoker.InvokeValueTask(YieldThenThrowWithResultAsync));

		Assert.IsNotNull(caught, "Invoke should rethrow an exception thrown after the ValueTask's first await.");
		Assert.AreEqual("boom", caught.Message);
	}

	[TestMethod]
	public void InvokeAsyncValueTaskOnOwnerThreadWaitsAndPropagates()
	{
		// Owner-thread fast path. Blocking is safe because nothing below needs DoInvokes to be pumped.
		Invoker invoker = new();
		StrongBox<bool> finished = new();

		invoker.InvokeValueTaskAsync(() => DelayThenSetAsync(finished)).GetAwaiter().GetResult();
		Assert.IsTrue(finished.Value, "On the owner thread, InvokeAsync should still wait for the ValueTask to finish.");

		Assert.AreEqual(42, invoker.InvokeValueTaskAsync(YieldThenReturnAsync).GetAwaiter().GetResult());
		Assert.AreEqual(42, invoker.InvokeValueTask(YieldThenReturnAsync));

		InvalidOperationException e = Assert.ThrowsExactly<InvalidOperationException>(() => invoker.InvokeValueTaskAsync(YieldThenThrowAsync).GetAwaiter().GetResult());
		Assert.AreEqual("boom", e.Message);
		e = Assert.ThrowsExactly<InvalidOperationException>(() => invoker.InvokeValueTask(YieldThenThrowAsync));
		Assert.AreEqual("boom", e.Message);
	}

	[TestMethod]
	public void InvokeValueTaskNullThrowsAtTheCallSite()
	{
		Invoker invoker = new();
		Func<ValueTask> valueTaskAction = null!;
		Func<ValueTask<int>> valueTaskFunction = null!;
		Assert.ThrowsExactly<ArgumentNullException>(() => _ = invoker.InvokeValueTaskAsync(valueTaskAction));
		Assert.ThrowsExactly<ArgumentNullException>(() => _ = invoker.InvokeValueTaskAsync(valueTaskFunction));
		Assert.ThrowsExactly<ArgumentNullException>(() => invoker.InvokeValueTask(valueTaskAction));
		Assert.ThrowsExactly<ArgumentNullException>(() => _ = invoker.InvokeValueTask(valueTaskFunction));
	}
}
