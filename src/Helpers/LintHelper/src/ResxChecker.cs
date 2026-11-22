using System.Text.RegularExpressions;

namespace AiDevHelpers.Helpers.LintHelper;

/// <summary>A problem in a Strings.resx. <see cref="Kind"/> is "duplicate", "missing" (used in code, not in the resx) or "unused".</summary>
public sealed record ResxFinding(string Resx, string Kind, string Key);

/// <summary>Checks every Resources/Strings.resx under a folder against the Strings.Get and Strings.Format calls in the .cs files beside it.</summary>
public static partial class ResxChecker
{
    public static IReadOnlyList<ResxFinding> Check(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(Strings.Format("FolderNotFound", root));
        }

        var fullRoot = Path.GetFullPath(root);
        var findings = new List<ResxFinding>();
        foreach (var resx in Directory.EnumerateFiles(fullRoot, "Strings.resx", SearchOption.AllDirectories))
        {
            var relativeResx = Path.GetRelativePath(fullRoot, resx);
            if (StringScanner.InBuildFolder(relativeResx))
            {
                continue;
            }

            var projectDir = Path.GetDirectoryName(Path.GetDirectoryName(resx))!;
            var names = DataName().Matches(File.ReadAllText(resx)).Select(m => m.Groups["name"].Value).ToList();
            var used = Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !StringScanner.InBuildFolder(Path.GetRelativePath(projectDir, f)))
                .SelectMany(f => StringsKey().Matches(File.ReadAllText(f)).Select(m => m.Groups["key"].Value))
                .ToHashSet(StringComparer.Ordinal);
            var known = names.ToHashSet(StringComparer.Ordinal);

            findings.AddRange(names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => new ResxFinding(relativeResx, "duplicate", g.Key)));
            findings.AddRange(used.Where(k => !known.Contains(k)).Order().Select(k => new ResxFinding(relativeResx, "missing", k)));
            findings.AddRange(known.Where(k => !used.Contains(k)).Order().Select(k => new ResxFinding(relativeResx, "unused", k)));
        }

        return findings;
    }

    [GeneratedRegex(@"<data name=""(?<name>[^""]+)""")]
    private static partial Regex DataName();

    [GeneratedRegex(@"Strings\.(?:Get|Format)\(""(?<key>[A-Za-z0-9_]+)""")]
    private static partial Regex StringsKey();
}
