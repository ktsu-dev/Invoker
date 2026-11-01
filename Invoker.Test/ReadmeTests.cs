// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.Invoker.Test;

using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Keeps README.md, which is also the nuget.org package page, in step with the public API (#70).
/// </summary>
[TestClass]
public partial class ReadmeTests
{
	private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

	[GeneratedRegex(@"```csharp\r?\n(?<code>.*?)```", RegexOptions.Singleline)]
	private static partial Regex CSharpBlock();

	[GeneratedRegex(@"\b_?invoker\.(?<member>\w+)", RegexOptions.IgnoreCase)]
	private static partial Regex InvokerMemberAccess();

	[GeneratedRegex(@"\boverride\s+[\w<>?]+\s+(?<member>\w+)\s*\(")]
	private static partial Regex OverrideDeclaration();

	[GeneratedRegex(@"^### `Invoker` Class\r?\n(?<section>.*?)^## ", RegexOptions.Singleline | RegexOptions.Multiline)]
	private static partial Regex InvokerApiSection();

	[GeneratedRegex(@"^\| `(?<name>\w+)(?:<\w+>)?` \|(?<rest>.*)$", RegexOptions.Multiline)]
	private static partial Regex ApiTableRow();

	private static string ReadReadme()
	{
		for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			if (File.Exists(Path.Join(directory.FullName, "Invoker.sln")))
			{
				return File.ReadAllText(Path.Join(directory.FullName, "README.md"));
			}
		}

		throw new FileNotFoundException("Invoker.sln was not found above the test output directory.");
	}

	private static List<string> CSharpCode() => [.. CSharpBlock().Matches(ReadReadme()).Select(match => match.Groups["code"].Value)];

	[TestMethod]
	public void EveryInvokerMemberUsedInReadmeSamplesExists()
	{
		List<string> code = CSharpCode();
		Assert.IsNotEmpty(code);

		List<string> missing = [.. code
			.SelectMany(block => InvokerMemberAccess().Matches(block))
			.Select(access => access.Groups["member"].Value)
			.Where(member => typeof(Invoker).GetMember(member, PublicInstance).Length == 0)
			.Distinct()];

		Assert.IsEmpty(missing, $"README samples use Invoker members that don't exist: {string.Join(", ", missing)}");
	}

	[TestMethod]
	public void EveryMemberOverriddenInReadmeSamplesIsOverridable()
	{
		List<string> missing = [.. CSharpCode()
			.SelectMany(block => OverrideDeclaration().Matches(block))
			.Select(declaration => declaration.Groups["member"].Value)
			.Where(member => !typeof(Invoker)
				.GetMethods(PublicInstance | BindingFlags.NonPublic)
				.Any(method => method.Name == member && method.IsVirtual && !method.IsFinal && !method.IsPrivate))
			.Distinct()];

		Assert.IsEmpty(missing, $"README samples override Invoker members that aren't virtual: {string.Join(", ", missing)}");
	}

	[TestMethod]
	public void EveryMemberInTheReadmeApiReferenceExistsWithItsDocumentedReturnType()
	{
		Match section = InvokerApiSection().Match(ReadReadme());
		Assert.IsTrue(section.Success, "The README has no \"### `Invoker` Class\" section.");

		List<Match> rows = [.. ApiTableRow().Matches(section.Groups["section"].Value).Cast<Match>()];
		Assert.IsNotEmpty(rows);

		Dictionary<string, Type> plainReturnTypes = new()
		{
			["void"] = typeof(void),
			["bool"] = typeof(bool),
			["Task"] = typeof(Task),
		};

		List<string> problems = [];
		foreach (Match row in rows)
		{
			string name = row.Groups["name"].Value;
			MemberInfo[] members = typeof(Invoker).GetMember(name, PublicInstance);
			if (members.Length == 0)
			{
				problems.Add($"{name} does not exist");
				continue;
			}

			// Method rows are | Name | Parameters | Return Type | Description |; the return type is the second cell.
			string[] cells = row.Groups["rest"].Value.Split('|', StringSplitOptions.TrimEntries);
			MethodInfo[] methods = [.. members.OfType<MethodInfo>()];
			if (methods.Length > 0 && cells.Length > 2
				&& plainReturnTypes.TryGetValue(cells[1].Trim('`'), out Type? documented)
				&& !methods.Any(method => method.ReturnType == documented))
			{
				problems.Add($"{name} is documented as returning {cells[1]}");
			}
		}

		Assert.IsEmpty(problems, string.Join("; ", problems));
	}

	[TestMethod]
	[Timeout(10_000, CooperativeCancellation = true)]
	public void ReadmeAsynchronousInvocationSampleCompletesWhilePumping()
	{
		Invoker invoker = new();
		List<string> ran = [];

		Task background = Task.Run(async () =>
		{
			Assert.IsTrue(invoker.TryBeginInvoke(() => ran.Add("background")));
			string result = await invoker.InvokeAsync(() => "Result from main thread").ConfigureAwait(false);
			ran.Add(result);
		});

		while (!background.IsCompleted)
		{
			invoker.DoInvokes();
			Thread.Yield();
		}

		background.GetAwaiter().GetResult();
		CollectionAssert.AreEqual(new List<string> { "background", "Result from main thread" }, ran);
	}
}
