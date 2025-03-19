namespace ktsu.Invoker;

internal class Sample
{
	internal static async Task Main()
	{
		ContextDependent contextDependent = new();

		// Invoking from the owning thread will execute the delegate immediately, bypassing the queue
		contextDependent.Invoker.Invoke(ContextDependent.DoWork);

		// Invoking from a different thread will queue the delegate to be executed by the owning thread
		var queuedTask = Task.Run(() => contextDependent.Invoker.Invoke(ContextDependent.DoWork));

		// Call DoInvokes() on the owning thread to consume and execute queued tasks
		contextDependent.Invoker.DoInvokes();

		try
		{
			// Attempting to consume queued tasks from a different thread will throw an InvalidOperationException
			await Task.Run(contextDependent.Invoker.DoInvokes);
		}
		catch (InvalidOperationException) { }

		// Invoking a delegate with a return value
		int result = contextDependent.Invoker.Invoke(ContextDependent.DoWorkAndReturn);

		// Async overloads are also available
		await contextDependent.Invoker.InvokeAsync(ContextDependent.DoWork);
		result = await contextDependent.Invoker.InvokeAsync(ContextDependent.DoWorkAndReturn);
	}
}

internal class ContextDependent
{
	internal Invoker Invoker { get; } = new();

	internal static void DoWork() => Console.WriteLine("Hello, world!");

	internal static int DoWorkAndReturn() => 42;
}
