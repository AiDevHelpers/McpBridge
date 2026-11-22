using System.Text.RegularExpressions;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

/// <summary>
/// Localization gate (Phase 7, Q3): user-facing text in the helper libraries lives in Resources/Strings.resx, not in code.
/// This finds multi-word string literals in helper sources. A literal that is not user-facing (a format pattern, a keyword
/// list, a log template) is listed in hardcoded-strings-allowlist.txt as <c>Helper|literal</c>, with a reason in a comment line above.
/// </summary>
public sealed partial class HardCodedStringTests
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
        // Every helper library, and the MCP Core library whose tool replies a client reads.
        var helpers = Path.Combine(RepoRoot(), "src", "Helpers");
        var roots = Directory.EnumerateDirectories(helpers)
            .Select(dir => (Name: Path.GetFileName(dir), Src: Path.Combine(dir, "src")))
            .Append((Name: "MCP.Core", Src: Path.Combine(RepoRoot(), "src", "MCP", "Core", "src")));
        foreach (var (helper, src) in roots)
        {
            if (!Directory.Exists(src))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(src, file);
                if (relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(file) == "Strings.cs")
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var trimmed = line.TrimStart();
                    // Skip comments, attributes (tool and parameter descriptions are for the client's tool list), and
                    // the continuation lines of a multi-line description.
                    var isDescriptionContinuation = trimmed.StartsWith('"') && (trimmed.EndsWith('+') || trimmed.EndsWith(")]"));
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("///") || trimmed.StartsWith("[") || trimmed.StartsWith("*")
                        || isDescriptionContinuation || LoggerCall().IsMatch(line))
                    {
                        continue;
                    }

                    foreach (Match m in Literal().Matches(line))
                    {
                        var text = m.Groups["text"].Value;
                        if (MultiWord().IsMatch(text))
                        {
                            yield return (helper, relative, i + 1, text);
                        }
                    }
                }
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
        var helpers = Path.Combine(RepoRoot(), "src", "Helpers");
        var missing = new List<string>();
        foreach (var helperDir in Directory.EnumerateDirectories(helpers))
        {
            var src = Path.Combine(helperDir, "src");
            var resx = Path.Combine(src, "Resources", "Strings.resx");
            if (!Directory.Exists(src))
            {
                continue;
            }

            var keys = File.Exists(resx)
                ? DataName().Matches(File.ReadAllText(resx)).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal)
                : [];
            foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                         .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            {
                foreach (Match use in StringsKey().Matches(File.ReadAllText(file)))
                {
                    var key = use.Groups["key"].Value;
                    if (!keys.Contains(key))
                    {
                        missing.Add($"{Path.GetFileName(helperDir)}: Strings key '{key}' used in {Path.GetFileName(file)} is not in Resources/Strings.resx");
                    }
                }
            }
        }

        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    [GeneratedRegex(@"<data name=""(?<name>[^""]+)""")]
    private static partial Regex DataName();

    [GeneratedRegex(@"Strings\.(?:Get|Format)\(""(?<key>[A-Za-z0-9_]+)""")]
    private static partial Regex StringsKey();

    [GeneratedRegex(@"\$?""(?<text>(?:[^""\\]|\\.)*)""")]
    private static partial Regex Literal();

    [GeneratedRegex(@"[A-Za-z]{2,} [A-Za-z]{2,}")]
    private static partial Regex MultiWord();

    [GeneratedRegex(@"Log(Information|Warning|Debug|Error|Trace|Critical)\(")]
    private static partial Regex LoggerCall();
}
