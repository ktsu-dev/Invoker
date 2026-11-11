// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.Invoker.Test;

using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Once the owner thread has exited, .NET may hand its managed thread ID to a new thread. That thread
/// is not the owner and must not be treated as one (#103).
/// </summary>
[TestClass]
[DoNotParallelize]
public class OwnerThreadIdentityTests
{
	private const int Attempts = 50;

	[TestMethod]
	public void ThreadsStartedAfterTheOwnerExitsNeverPassTheOwnerCheck()
	{
		(Invoker invoker, int ownerId) = CreateOnThreadThatExits();

		for (int attempt = 0; attempt < Attempts; attempt++)
		{
			// Let the dead owner's Thread object be collected, which is when its ID is recycled.
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();

			Probe probe = RunProbe(invoker);

			Assert.IsFalse(probe.InvokeAsyncRanInline, $"Attempt {attempt}: InvokeAsync ran inline on thread {probe.ThreadId} (owner was {ownerId}).");
			Assert.IsFalse(probe.TryBeginInvokeRanInline, $"Attempt {attempt}: TryBeginInvoke ran inline on thread {probe.ThreadId} (owner was {ownerId}).");
			Assert.IsTrue(probe.DoInvokesThrew, $"Attempt {attempt}: DoInvokes was accepted from thread {probe.ThreadId} (owner was {ownerId}).");
		}
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static (Invoker Invoker, int OwnerId) CreateOnThreadThatExits()
	{
		Invoker? invoker = null;
		int ownerId = 0;
		Thread owner = new(() =>
		{
			invoker = new Invoker();
			ownerId = Environment.CurrentManagedThreadId;
		});
		owner.Start();
		owner.Join();
		return (invoker!, ownerId);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static Probe RunProbe(Invoker invoker)
	{
		Probe probe = new();
		Thread thread = new(() =>
		{
			probe.ThreadId = Environment.CurrentManagedThreadId;

			Task pending = invoker.InvokeAsync(() => { });
			probe.InvokeAsyncRanInline = pending.IsCompleted;

			bool ranInline = false;
			invoker.TryBeginInvoke(() => ranInline = true);
			probe.TryBeginInvokeRanInline = ranInline;

			try
			{
				invoker.DoInvokes();
			}
			catch (InvalidOperationException)
			{
				probe.DoInvokesThrew = true;
			}
		});
		thread.Start();
		thread.Join();
		return probe;
	}

	private sealed class Probe
	{
		public int ThreadId { get; set; }

		public bool InvokeAsyncRanInline { get; set; }

		public bool TryBeginInvokeRanInline { get; set; }

		public bool DoInvokesThrew { get; set; }
	}
}
