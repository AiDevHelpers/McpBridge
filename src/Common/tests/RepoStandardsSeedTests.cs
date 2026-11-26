using System.Xml.Linq;
using Xunit;

namespace AiDevHelpers.Common.Tests;

/// <summary>The committed standards for this repo must allow or except every project's target framework, and no exception may be stale.</summary>
public sealed class RepoStandardsSeedTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AiDevHelpers.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("AiDevHelpers.slnx not found above the test output folder.");
    }

    private static IEnumerable<(string Path, string[] Frameworks)> Projects(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                // Test fixtures are sample inputs, not repo projects; one is deliberately malformed.
                && !p.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}Fixtures{Path.DirectorySeparatorChar}"))
            .Select(p =>
            {
                var doc = XDocument.Load(p);
                var single = doc.Descendants("TargetFramework").Select(e => e.Value.Trim());
                var multi = doc.Descendants("TargetFrameworks").SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                return (Path.GetRelativePath(root, p).Replace('\\', '/'), single.Concat(multi).Distinct().ToArray());
            });

    [Fact]
    public void EveryProjectFrameworkIsAllowedOrExcepted()
    {
        var root = RepoRoot();
        var standards = ProjectStandardsFile.Read(root);
        Assert.Empty(standards.Warnings);
        var rules = standards.Standards!;

        var unmatched = new List<string>();
        foreach (var (path, frameworks) in Projects(root))
        {
            foreach (var framework in frameworks)
            {
                var allowed = framework == rules.BaseFramework
                    || rules.AllowedPlatformSuffixes.Any(s => framework == rules.BaseFramework + s);
                var excepted = rules.Exceptions.Any(e => e.Project == path && e.Framework == framework);
                if (!allowed && !excepted)
                {
                    unmatched.Add($"{path} {framework}");
                }
            }
        }

        Assert.True(unmatched.Count == 0, "Not allowed or excepted: " + string.Join(", ", unmatched));
    }

    [Fact]
    public void EveryExceptionNamesAnExistingProjectThatTargetsIt()
    {
        var root = RepoRoot();
        var rules = ProjectStandardsFile.Read(root).Standards!;
        var projects = Projects(root).ToDictionary(p => p.Path, p => p.Frameworks);

        var stale = rules.Exceptions
            .Where(e => !projects.TryGetValue(e.Project, out var fws) || !fws.Contains(e.Framework))
            .Select(e => $"{e.Project} {e.Framework}")
            .ToList();

        Assert.True(stale.Count == 0, "Stale exceptions: " + string.Join(", ", stale));
    }
}
