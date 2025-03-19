# ktsu.Invoker

ktsu.Invoker is a .NET library that provides methods to ensure delegates are executed on the intended thread. It is designed to simplify task execution and thread management in .NET applications where delegates are required to run within a specific context, such as the UI thread in WPF or WinForms applications, or the window thread in OpenGL or DirectX applications.

## Installation

To install Invoker, you can use the .NET CLI:

```bash
dotnet add package ktsu.Invoker
```

Or you can use the NuGet Package Manager in Visual Studio to search for and install the ktsu.Invoker package.

## Usage

See the [Sample](Sample/Sample.cs) project for a complete example including Async overloads.

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

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE.md) file for details.

## Contributing

Contributions are welcome! Please open an issue or submit a pull request for any improvements or bug fixes.

## Acknowledgements

Special thanks to all contributors and the .NET community for their support.
