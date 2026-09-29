// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.Invoker;

/// <summary>
/// Carries the exception thrown by an action queued with <see cref="Invoker.TryBeginInvoke(Action)"/>.
/// </summary>
/// <param name="exception">The exception the action threw.</param>
public class BeginInvokeFailedEventArgs(Exception exception) : EventArgs
{
	/// <summary>
	/// Gets the exception the action threw.
	/// </summary>
	public Exception Exception { get; } = exception;
}
