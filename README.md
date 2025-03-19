# ktsu.Invoker

ktsu.Invoker is a .NET library that provides methods to ensure delegates are executed on the intended thread. It is designed to simplify task execution and thread management in .NET applications where delegates are required to run within a specific context, such as the UI thread in WPF or WinForms applications, or the window thread in OpenGL or DirectX applications.

## Installation

To install Invoker, you can use the .NET CLI:

```bash
dotnet add package ktsu.Invoker
```

Or you can use the NuGet Package Manager in Visual Studio to search for and install the ktsu.Invoker package.

## Usage

### Synchronous Invocation

```csharp
using ktsu.Invoker;


```

### Asynchronous Invocation

```csharp
using ktsu.Invoker;

var invoker = new Invoker(); await invoker.InvokeAsync(() => Console.WriteLine("Hello, World!"));
```




### Synchronous Function Invocation with Return Value

```csharp
using ktsu.Invoker;

var invoker = new Invoker(); var result = invoker.Invoke(() => "Hello, World!");
```

### Asynchronous Function Invocation with Return Value
```csharp
using ktsu.Invoker;

var invoker = new Invoker(); var result = await invoker.InvokeAsync(() => "Hello, World!");
```


### Executing All Queued Tasks

```csharp
using ktsu.Invoker;

var invoker = new Invoker(); invoker.Invoke(() => Console.WriteLine("Hello, World!"));
```

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE.md) file for details.

## Contributing

Contributions are welcome! Please open an issue or submit a pull request for any improvements or bug fixes.

## Acknowledgements

Special thanks to all contributors and the .NET community for their support.
