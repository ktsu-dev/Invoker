// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.Invoker.Test;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Compiles consumer code against the library at older language versions (#96). C# 12 is the default for net8.0,
/// and C# 7.3 for netstandard2.0 and .NET Framework. Neither honours [OverloadResolutionPriority], so an overload
/// set that only resolves through it is a compile error for those consumers, though this net10.0 test project
/// never sees one.
/// </summary>
[TestClass]
public class LanguageVersionTests
{
	private const string Consumer = """
		using System.Threading.Tasks;

		public static class Consumer
		{
			public static async Task RunAsync(ktsu.Invoker.Invoker inv)
			{
				await inv.InvokeAsync(async () => { await Task.Delay(1); });
				int r = await inv.InvokeAsync(async () => { await Task.Delay(1); return 5; });
				inv.Invoke(async () => { await Task.Delay(1); });
				int s = inv.Invoke(async () => { await Task.Delay(1); return 5; });
				await inv.InvokeAsync(() => Task.CompletedTask);
				int t = await inv.InvokeAsync(() => 5);
			}
		}
		""";

	private static readonly string[] ExpectedOverloads =
	[
		"InvokeAsync(System.Func<System.Threading.Tasks.Task>)",
		"InvokeAsync<int>(System.Func<System.Threading.Tasks.Task<int>>)",
		"Invoke(System.Func<System.Threading.Tasks.Task>)",
		"Invoke<int>(System.Func<System.Threading.Tasks.Task<int>>)",
		"InvokeAsync(System.Func<System.Threading.Tasks.Task>)",
		"InvokeAsync<int>(System.Func<int>)",
	];

	[TestMethod]
	[DataRow(LanguageVersion.CSharp7_3)]
	[DataRow(LanguageVersion.CSharp12)]
	[DataRow(LanguageVersion.CSharp13)]
	[DataRow(LanguageVersion.Latest)]
	public void AsyncLambdasBindToTheTaskOverloads(LanguageVersion languageVersion)
	{
		SyntaxTree tree = CSharpSyntaxTree.ParseText(Consumer, new CSharpParseOptions(languageVersion));
		CSharpCompilation compilation = Compile(tree);

		string[] errors = Errors(compilation);
		Assert.IsEmpty(errors, string.Join(Environment.NewLine, errors));
		CollectionAssert.AreEqual(ExpectedOverloads, BoundInvokerOverloads(compilation, tree));
	}

	private static CSharpCompilation Compile(SyntaxTree tree) =>
		CSharpCompilation.Create(
			"Consumer",
			[tree],
			References(),
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

	private static string[] Errors(Compilation compilation) =>
		[.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())];

	private static string[] BoundInvokerOverloads(Compilation compilation, SyntaxTree tree)
	{
		SemanticModel model = compilation.GetSemanticModel(tree);
		return [.. tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
			.Select(call => model.GetSymbolInfo(call).Symbol)
			.OfType<IMethodSymbol>()
			.Where(method => method.ContainingType.Name == nameof(Invoker))
			.Select(Describe)];
	}

	private static string Describe(IMethodSymbol method) =>
		$"{method.Name}{TypeArguments(method)}({string.Join(", ", method.Parameters.Select(p => p.Type.ToDisplayString()))})";

	private static string TypeArguments(IMethodSymbol method) =>
		method.TypeArguments.Length == 0 ? string.Empty : $"<{string.Join(", ", method.TypeArguments.Select(t => t.ToDisplayString()))}>";

	private static IEnumerable<MetadataReference> References()
	{
		string trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
		return trusted.Split(Path.PathSeparator)
			.Select(path => MetadataReference.CreateFromFile(path))
			.Append(MetadataReference.CreateFromFile(typeof(Invoker).Assembly.Location));
	}
}
