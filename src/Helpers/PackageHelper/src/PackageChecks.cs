using System.Text.RegularExpressions;

namespace AiDevHelpers.Helpers.PackageHelper;

/// <summary>One package-setup finding. Severity is error, warning or info.</summary>
public sealed record PackageFinding(string Ecosystem, string Severity, string Path, string Message);

/// <summary>
/// Checks package setup (D3): NuGet (nuget.config, Directory.Packages.props, xunit analyzers in test projects,
/// .NET analyzers enabled), pip (pinned requirements) and npm (a lockfile next to package.json).
/// Pure file checks; nothing is installed or restored.
/// </summary>
public static partial class PackageChecks
{
    private static readonly string[] SkipFolders = ["bin", "obj", "node_modules", "dist", ".git"];

    public static IReadOnlyList<PackageFinding> Analyze(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Folder not found: {root}");
        }

        var findings = new List<PackageFinding>();
        CheckNuGet(root, findings);
        CheckPip(root, findings);
        CheckNpm(root, findings);
        return findings;
    }

    private static void CheckNuGet(string root, List<PackageFinding> findings)
    {
        var projects = Enumerate(root, "*.csproj").ToList();
        if (projects.Count == 0)
        {
            return;
        }

        if (!Enumerate(root, "nuget.config").Any())
        {
            findings.Add(new PackageFinding("nuget", "info", root, "No nuget.config. Package sources come from the machine defaults."));
        }

        if (!Enumerate(root, "Directory.Packages.props").Any())
        {
            findings.Add(new PackageFinding("nuget", "info", root, "No Directory.Packages.props. Package versions are not centrally managed."));
        }

        foreach (var project in projects)
        {
            var text = File.ReadAllText(project);
            var isTest = text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase) || text.Contains("<IsTestProject>true</IsTestProject>", StringComparison.OrdinalIgnoreCase);
            if (isTest && !text.Contains("xunit.analyzers", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new PackageFinding("nuget", "warning", project, "Test project without the xunit.analyzers package."));
            }

            if (DisablesNetAnalyzers().IsMatch(text))
            {
                findings.Add(new PackageFinding("nuget", "warning", project, "EnableNETAnalyzers is false. The .NET analyzers (Microsoft.CodeAnalysis.NetAnalyzers) are off."));
            }
        }
    }

    private static void CheckPip(string root, List<PackageFinding> findings)
    {
        foreach (var requirements in Enumerate(root, "requirements.txt"))
        {
            foreach (var raw in File.ReadAllLines(requirements))
            {
                var line = raw.Split('#')[0].Trim();
                if (line.Length == 0 || line.StartsWith('-'))
                {
                    continue;
                }

                if (!line.Contains("==", StringComparison.Ordinal))
                {
                    findings.Add(new PackageFinding("pip", "warning", requirements, $"'{line}' is not pinned with ==."));
                }
            }
        }
    }

    private static void CheckNpm(string root, List<PackageFinding> findings)
    {
        foreach (var package in Enumerate(root, "package.json"))
        {
            var dir = Path.GetDirectoryName(package)!;
            var hasLock = File.Exists(Path.Combine(dir, "package-lock.json"))
                          || File.Exists(Path.Combine(dir, "npm-shrinkwrap.json"))
                          || File.Exists(Path.Combine(dir, "yarn.lock"))
                          || File.Exists(Path.Combine(dir, "pnpm-lock.yaml"));
            if (!hasLock)
            {
                findings.Add(new PackageFinding("npm", "error", package, "No lockfile next to package.json. npm ci needs one."));
            }
        }
    }

    private static IEnumerable<string> Enumerate(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => SkipFolders.Contains(part, StringComparer.OrdinalIgnoreCase)));

    [GeneratedRegex(@"<EnableNETAnalyzers>\s*false\s*</EnableNETAnalyzers>", RegexOptions.IgnoreCase)]
    private static partial Regex DisablesNetAnalyzers();
}
