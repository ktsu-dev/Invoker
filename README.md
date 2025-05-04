# ktsu.Invoker

> A .NET library that ensures delegates are executed on the intended thread, simplifying thread management in UI and graphics applications.

[![License](https://img.shields.io/github/license/ktsu-dev/Invoker)](https://github.com/ktsu-dev/Invoker/blob/main/LICENSE.md)
[![NuGet](https://img.shields.io/nuget/v/ktsu.Invoker.svg)](https://www.nuget.org/packages/ktsu.Invoker/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ktsu.Invoker.svg)](https://www.nuget.org/packages/ktsu.Invoker/)
[![Build Status](https://github.com/ktsu-dev/Invoker/workflows/build/badge.svg)](https://github.com/ktsu-dev/Invoker/actions)
[![GitHub Stars](https://img.shields.io/github/stars/ktsu-dev/Invoker?style=social)](https://github.com/ktsu-dev/Invoker/stargazers)

## Introduction

Invoker is a .NET library that provides methods to ensure delegates are executed on the intended thread. It is designed to simplify task execution and thread management in .NET applications where delegates are required to run within a specific context, such as the UI thread in WPF or WinForms applications, or the window thread in OpenGL or DirectX applications.

## Features

- **Thread-Safe Execution**: Ensure delegates run on the intended thread
- **Synchronous & Asynchronous Support**: Both blocking and non-blocking invocation patterns
- **Return Value Support**: Easily retrieve results from cross-thread operations
- **Immediate Execution**: Auto-detect if already on the target thread for optimal performance
- **Queue Management**: Built-in task queue with controlled execution timing
- **Thread Ownership**: Clear ownership model for execution contexts
- **Exception Propagation**: Properly propagates exceptions across thread boundaries
- **Lightweight Design**: Minimal overhead for performance-critical applications

## Installation

### Package Manager Console

```powershell
Install-Package ktsu.Invoker
```

### .NET CLI

```bash
dotnet add package ktsu.Invoker
```

### Package Reference

```xml
<PackageReference Include="ktsu.Invoker" Version="x.y.z" />
```

## Usage Examples

### Basic Example

```csharp
// Initialize an instance on the owning thread
var invoker = new Invoker();

// Queue a task from a different thread, which blocks until the delegate has been executed via DoInvokes() on the owning thread
invoker.Invoke(() => Console.WriteLine("Hello, World!"));

// Call DoInvokes() on the owning thread to execute queued tasks
invoker.DoInvokes();

// NOTE: If you queue from the owning thread the delegate will be executed immediately, bypassing DoInvokes()

// Delegates with return values are supported
string result = invoker.Invoke(() => "Hello, World!");
```

### WPF UI Thread Example

```csharp
using System.Windows;
using ktsu.Invoker;

public partial class MainWindow : Window
{
    private readonly Invoker _invoker = new Invoker();
    
    public MainWindow()
    {
        InitializeComponent();
        
        // Start a background operation
        Task.Run(() => BackgroundOperation());
    }
    
    private async Task BackgroundOperation()
    {
        // Simulate work
        await Task.Delay(1000);
        
        // Update UI from background thread safely
        _invoker.Invoke(() => {
            StatusTextBlock.Text = "Operation Completed!";
            ResultListBox.Items.Add("Background task result");
        });
    }
    
    // Call this in your UI event loop or dispatcher
    private void ProcessEvents()
    {
        _invoker.DoInvokes();
    }
}
```

### Asynchronous Invocation

```csharp
using ktsu.Invoker;

// Create invoker on the main thread
var invoker = new Invoker();

// From background thread
await Task.Run(async () => {
    // Queue task and continue without waiting
    invoker.BeginInvoke(() => Console.WriteLine("Processing in the background"));
    
    // Queue task and get Task for completion
    Task<string> resultTask = invoker.InvokeAsync(() => "Result from main thread");
    
    // Await the result
    string result = await resultTask;
    Console.WriteLine($"Got result: {result}");
});

// On main thread, execute pending operations
invoker.DoInvokes();
```

### Game Loop Integration

```csharp
using ktsu.Invoker;

public class Game
{
    private readonly Invoker _invoker = new Invoker();
    
    public void Run()
    {
        // Start rendering on the main thread
        while (true)
        {
            // Execute any queued operations from other threads
            _invoker.DoInvokes();
            
            // Perform rendering
            Render();
            
            // Process events, etc.
        }
    }
    
    private void Render() 
    {
        // OpenGL/DirectX rendering code here
    }
    
    // Call this from other threads
    public void QueueTextureLoad(string texturePath)
    {
        // OpenGL/DirectX resources often need to be created on the main thread
        _invoker.BeginInvoke(() => LoadTextureOnMainThread(texturePath));
    }
    
    private void LoadTextureOnMainThread(string texturePath)
    {
        // Load texture using OpenGL/DirectX APIs
    }
}
```

## API Reference

### `Invoker` Class

The main class that manages execution of delegates on the intended thread.

#### Properties

| Name | Type | Description |
|------|------|-------------|
| `IsInvokerThread` | `bool` | Returns true if the current thread is the thread that owns the invoker |
| `HasPendingInvokes` | `bool` | Returns true if there are any pending invocations waiting to be processed |

#### Methods

| Name | Parameters | Return Type | Description |
|------|------------|-------------|-------------|
| `Invoke<T>` | `Func<T> func` | `T` | Executes the function on the owner thread and returns its result, blocking if called from another thread |
| `Invoke` | `Action action` | `void` | Executes the action on the owner thread, blocking if called from another thread |
| `BeginInvoke` | `Action action` | `void` | Queues an action to be executed on the owner thread without waiting for completion |
| `InvokeAsync<T>` | `Func<T> func` | `Task<T>` | Queues a function to be executed on the owner thread and returns a Task that completes with the result |
| `InvokeAsync` | `Action action` | `Task` | Queues an action to be executed on the owner thread and returns a Task that completes when the action is done |
| `DoInvokes` | | `void` | Processes all pending invocations (must be called from the owner thread) |

## Advanced Usage

### Thread Synchronization

Invoker provides a clean way to synchronize access to resources that must be accessed from a specific thread:

```csharp
public class ResourceManager
{
    private readonly Invoker _invoker = new Invoker();
    private readonly Dictionary<string, Resource> _resources = new Dictionary<string, Resource>();
    
    // This method can be called from any thread
    public Resource GetResource(string id)
    {
        return _invoker.Invoke(() => {
            if (!_resources.TryGetValue(id, out var resource))
            {
                resource = new Resource(id);
                _resources[id] = resource;
            }
            return resource;
        });
    }
    
    // Call this regularly on the owner thread
    public void Update()
    {
        _invoker.DoInvokes(); // Process any pending resource requests
        
        // Update resources
        foreach (var resource in _resources.Values)
        {
            resource.Update();
        }
    }
}
```

### Custom Thread Identification

Sometimes you may need custom thread identification logic:

```csharp
public class CustomInvoker : Invoker
{
    private readonly int _targetThreadId;
    
    public CustomInvoker(int targetThreadId)
    {
        _targetThreadId = targetThreadId;
    }
    
    // Override to provide custom thread identification
    protected override bool IsOwnerThread()
    {
        return Thread.CurrentThread.ManagedThreadId == _targetThreadId;
    }
}
```

## Contributing

Contributions are welcome! Here's how you can help:

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add some amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

Please make sure to update tests as appropriate and adhere to the existing coding style.

## License

This project is licensed under the MIT License - see the [LICENSE.md](LICENSE.md) file for details.

## Acknowledgements

Special thanks to all contributors and the .NET community for their support.
