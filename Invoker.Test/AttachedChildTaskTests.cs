// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.Invoker.Test;

using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// A child task the delegate starts with <see cref="TaskCreationOptions.AttachedToParent"/> must not
/// hold up or fault an off-thread call, because the same call on the owner thread runs inline with no
/// task to attach to (#92).
/// </summary>
[TestClass]
public class AttachedChildTaskTests
{
	private static readonly TimeSpan ReleaseDelay = TimeSpan.FromSeconds(2);

	[TestMethod]
	public void OffThreadInvokeDoesNotWaitForAnAttachedChild()
	{
		Invoker invoker = new();
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Task? child = null;
		bool returnedBeforeRelease = false;
		Task<bool> releaser = ReleaseLater(release);

		Exception? thrown = RunOnWorkerWhilePumping(invoker, () =>
		{
			invoker.Invoke(() =>
			{
				child = Task.Factory.StartNew(() => release.Task.Wait(), CancellationToken.None, TaskCreationOptions.AttachedToParent | TaskCreationOptions.LongRunning, TaskScheduler.Default);
			});
			returnedBeforeRelease = !release.Task.IsCompleted;
		});

		releaser.Wait();
		child?.Wait();
		Assert.IsNull(thrown, $"Invoke should not throw, but threw {thrown}");
		Assert.IsTrue(returnedBeforeRelease, "Invoke should return without waiting for the attached child.");
	}

	[TestMethod]
	public void OffThreadInvokeFunctionDoesNotWaitForAnAttachedChild()
	{
		Invoker invoker = new();
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Task? child = null;
		bool returnedBeforeRelease = false;
		int result = 0;
		Task<bool> releaser = ReleaseLater(release);

		Exception? thrown = RunOnWorkerWhilePumping(invoker, () =>
		{
			result = invoker.Invoke(() =>
			{
				child = Task.Factory.StartNew(() => release.Task.Wait(), CancellationToken.None, TaskCreationOptions.AttachedToParent | TaskCreationOptions.LongRunning, TaskScheduler.Default);
				return 42;
			});
			returnedBeforeRelease = !release.Task.IsCompleted;
		});

		releaser.Wait();
		child?.Wait();
		Assert.IsNull(thrown, $"Invoke should not throw, but threw {thrown}");
		Assert.IsTrue(returnedBeforeRelease, "Invoke should return without waiting for the attached child.");
		Assert.AreEqual(42, result);
	}

	// The child waits on the release with no timeout, so something other than the owner thread has to set
	// it: if the queued task waited for its children, DoInvokes would block on the child too. The children
	// are LongRunning so that, blocked, they don't hold the pool threads Invoke's own continuation needs.
	private static Task<bool> ReleaseLater(TaskCompletionSource release) =>
		Task.Delay(ReleaseDelay).ContinueWith(_ => release.TrySetResult(), TaskScheduler.Default);

	[TestMethod]
	public void OffThreadInvokeDoesNotThrowAnAttachedChildsException()
	{
		Invoker invoker = new();
		Task? child = null;

		Exception? thrown = RunOnWorkerWhilePumping(invoker, () => invoker.Invoke(() =>
		{
			child = Task.Factory.StartNew(() => throw new InvalidTimeZoneException("child"), CancellationToken.None, TaskCreationOptions.AttachedToParent, TaskScheduler.Default);
		}));

		Assert.IsNull(thrown, $"Invoke should not throw the child's exception, but threw {thrown}");
		Assert.ThrowsExactly<AggregateException>(() => child!.Wait(), "The child should still fault on its own.");
	}

	[TestMethod]
	public async Task OffThreadInvokeAsyncDoesNotThrowAnAttachedChildsException()
	{
		Invoker invoker = new();
		Task? child = null;

		Task<int> call = Task.Run(() => invoker.InvokeAsync(() =>
		{
			child = Task.Factory.StartNew(() => throw new InvalidTimeZoneException("child"), CancellationToken.None, TaskCreationOptions.AttachedToParent, TaskScheduler.Default);
			return 7;
		}));

		Assert.IsTrue(SpinWait.SpinUntil(() => !invoker.TaskQueue.IsEmpty, TimeSpan.FromSeconds(5)), "The call should queue its delegate.");
		invoker.DoInvokes();

		Assert.AreEqual(7, await call.ConfigureAwait(false));
		Assert.ThrowsExactly<AggregateException>(() => child!.Wait(), "The child should still fault on its own.");
	}

	/// <summary>
	/// Runs <paramref name="call"/> on a worker thread while this (owner) thread pumps
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
			catch (Exception e) when (e is AggregateException or InvalidTimeZoneException)
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
}
