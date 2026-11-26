using AiDevHelpers.Helpers.LocalizationHelper;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

/// <summary>
/// Localization gate (Phase 7, Q3): user-facing text in the helper libraries lives in Resources/Strings.resx, not in code.
/// This finds multi-word string literals in helper sources with <see cref="StringScanner"/>. A literal that is not user-facing
/// (a format pattern, a keyword list, a log template) is listed in hardcoded-strings-allowlist.txt as <c>Helper|literal</c>,
/// with a reason in a comment line above.
/// </summary>
public sealed class HardCodedStringTests
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

    private static HashSet<string> Allowlist()
    {
        var path = Path.Combine(RepoRoot(), "src", "MCP", "Core", "tests", "Tools", "hardcoded-strings-allowlist.txt");
        return File.Exists(path)
            ? File.ReadAllLines(path).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.TrimEnd()).ToHashSet(StringComparer.Ordinal)
            : [];
    }

    internal static IEnumerable<(string Helper, string File, int Line, string Literal)> Findings()
    {
        // Every helper library, the MCP Core library whose tool replies a client reads, and the Visual Studio bridge.
        var helpers = Path.Combine(RepoRoot(), "src", "Helpers");
        var roots = Directory.EnumerateDirectories(helpers)
            .Select(dir => (Name: Path.GetFileName(dir), Src: Path.Combine(dir, "src")))
            .Append((Name: "MCP.Core", Src: Path.Combine(RepoRoot(), "src", "MCP", "Core", "src")))
            .Append((Name: "Vs2026.Core", Src: Path.Combine(RepoRoot(), "src", "Installers", "Vs2026", "Core", "src")))
            .Append((Name: "Vs2026.Installer", Src: Path.Combine(RepoRoot(), "src", "Installers", "Vs2026", "Installer", "src")));
        foreach (var (helper, src) in roots)
        {
            if (!Directory.Exists(src))
            {
                continue;
            }

            foreach (var finding in StringScanner.Scan(src))
            {
                yield return (helper, finding.File, finding.Line, finding.Literal);
            }
        }
    }

    [Fact]
    public void NoUserFacingStringIsHardCodedInAHelper()
    {
        var allowed = Allowlist();

        var unlisted = Findings()
            .Where(f => !allowed.Contains($"{f.Helper}|{f.Literal.TrimEnd()}"))
            .Select(f => $"{f.Helper}/{f.File}:{f.Line}  {f.Literal}")
            .ToList();

        Assert.True(unlisted.Count == 0,
            "Move these to the helper's Resources/Strings.resx, or list them in hardcoded-strings-allowlist.txt with a reason:\n" + string.Join("\n", unlisted));
    }

    [Fact]
    public void EveryStringsKeyUsedInCodeExistsInTheHelpersResx()
    {
        var problems = ResxChecker.Check(Path.Combine(RepoRoot(), "src", "Helpers"))
            .Where(f => f.Kind is "missing" or "duplicate")
            .Select(f => $"{f.Resx}: {f.Kind} key '{f.Key}'")
            .ToList();

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
