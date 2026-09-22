// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.Invoker.Test;

using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Guards the identity every project derives from its solution-relative folder path.
/// ktsu.Sdk builds the assembly name and package ID from that path, so a project folder without the
/// repository's family name claims a name that belongs to no repository in particular - which is how
/// the demo project came to build as ktsu.Sample.
/// </summary>
[TestClass]
public partial class ProjectNamingTests
{
	private const string Family = "Invoker";
	private const string SolutionFileName = "Invoker.sln";

	/// <summary>
	/// Matches a solution project entry, capturing its display name and its project path.
	/// </summary>
	[GeneratedRegex("""^Project\("\{[^}]+\}"\) = "(?<name>[^"]+)", "(?<path>[^"]+\.csproj)""", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
	private static partial Regex SolutionProjectEntry();

	[TestMethod]
	public void EveryProjectFolderCarriesTheFamilyName()
	{
		DirectoryInfo root = FindSolutionRoot();
		List<FileInfo> projects = [.. EnumerateProjects(root)];

		Assert.IsTrue(projects.Count > 0, $"No projects found under {root.FullName}; the test is not looking where it thinks it is.");

		List<string> offenders = [.. projects
			.Select(project => Path.GetFileNameWithoutExtension(project.Name))
			.Where(identity => !IsFamilyName(identity))];

		Assert.AreEqual(
			0,
			offenders.Count,
			$"These projects derive an identity outside the ktsu.{Family} family: {string.Join(", ", offenders)}. " +
			$"A project folder must be named {Family} or {Family}.<Something>.");
	}

	[TestMethod]
	public void EveryProjectFileMatchesItsFolderName()
	{
		DirectoryInfo root = FindSolutionRoot();

		List<string> mismatches = [.. EnumerateProjects(root)
			.Where(project => Path.GetFileNameWithoutExtension(project.Name) != project.Directory!.Name)
			.Select(project => $"{project.Directory!.Name}/{project.Name}")];

		Assert.AreEqual(
			0,
			mismatches.Count,
			$"A project file must be named for its folder, because the folder is what decides the derived identity: {string.Join(", ", mismatches)}.");
	}

	[TestMethod]
	public void EverySolutionEntryMatchesItsProjectFile()
	{
		DirectoryInfo root = FindSolutionRoot();
		string solution = File.ReadAllText(Path.Join(root.FullName, SolutionFileName));
		MatchCollection entries = SolutionProjectEntry().Matches(solution);

		Assert.IsTrue(entries.Count > 0, $"No project entries found in {SolutionFileName}; the test is not reading what it thinks it is.");

		List<string> offenders = [];
		foreach (Match entry in entries)
		{
			string name = entry.Groups["name"].Value;
			string path = entry.Groups["path"].Value;
			// A solution always writes Windows separators, which are an ordinary character to
			// Path on Linux, where CI also runs.
			string localPath = path.Replace('\\', Path.DirectorySeparatorChar);
			string identity = Path.GetFileNameWithoutExtension(localPath);

			if (name != identity)
			{
				offenders.Add($"{name} is listed at {path}");
			}
			else if (!IsFamilyName(identity))
			{
				offenders.Add($"{name} is outside the ktsu.{Family} family");
			}
			// Path.Join, not Path.Combine: the path comes from the solution's own text, and a rooted
			// entry would make Combine drop the repository root and test a file outside it.
			else if (!File.Exists(Path.Join(root.FullName, localPath)))
			{
				offenders.Add($"{name} points at {path}, which does not exist");
			}
		}

		Assert.AreEqual(0, offenders.Count, $"Stale or misnamed {SolutionFileName} entries: {string.Join("; ", offenders)}.");
	}

	private static bool IsFamilyName(string identity) =>
		identity == Family || identity.StartsWith($"{Family}.", StringComparison.Ordinal);

	/// <summary>
	/// Projects sit one folder below the solution, so this deliberately does not recurse into bin and
	/// obj, where a build leaves copies of project files.
	/// </summary>
	private static IEnumerable<FileInfo> EnumerateProjects(DirectoryInfo root) =>
		root.EnumerateDirectories()
			.Where(directory => !directory.Name.StartsWith('.'))
			.SelectMany(directory => directory.EnumerateFiles("*.csproj"));

	private static DirectoryInfo FindSolutionRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Join(directory.FullName, SolutionFileName)))
		{
			directory = directory.Parent;
		}

		Assert.IsNotNull(directory, $"Could not find {SolutionFileName} above {AppContext.BaseDirectory}.");
		return directory;
	}
}
