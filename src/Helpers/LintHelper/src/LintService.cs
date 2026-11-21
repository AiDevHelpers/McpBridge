using System.Text.RegularExpressions;

namespace AiDevHelpers.Helpers.LintHelper;

/// <summary>Runs an external command. Injected so tests don't start dotnet.</summary>
public interface ICommandRunner
{
    (int ExitCode, string Output) Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory);
}

/// <summary>One mismatch or gap in the lint setup. Severity is error, warning or info.</summary>
public sealed record LintFinding(string Area, string Severity, string Path, string Message);

/// <summary>
/// Checks lint configuration for mismatches: .editorconfig against csproj NoWarn, tsconfig strict mode,
/// ESLint presence for TypeScript projects, deprecated TSLint, and optionally dotnet format verification.
/// </summary>
public sealed partial class LintService(ICommandRunner runner)
{
    private static readonly string[] SkipFolders = ["bin", "obj", "node_modules", "dist", ".git"];

    public IReadOnlyList<LintFinding> Analyze(string root, bool runDotnetFormat = false)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Folder not found: {root}");
        }

        var findings = new List<LintFinding>();
        CheckEditorConfig(root, findings);
        CheckNoWarnAgainstEditorConfig(root, findings);
        CheckTsConfigStrict(root, findings);
        CheckEslintAndTslint(root, findings);
        if (runDotnetFormat)
        {
            CheckDotnetFormat(root, findings);
        }

        return findings;
    }

    private static void CheckEditorConfig(string root, List<LintFinding> findings)
    {
        var path = Path.Combine(root, ".editorconfig");
        if (!File.Exists(path))
        {
            findings.Add(new LintFinding("editorconfig", "error", path, "No .editorconfig at the root. Analyzer severities and formatting have no shared source."));
            return;
        }

        if (!RootTrue().IsMatch(File.ReadAllText(path)))
        {
            findings.Add(new LintFinding("editorconfig", "warning", path, "Missing 'root = true'. Parent .editorconfig files may apply unexpectedly."));
        }
    }

    private static void CheckNoWarnAgainstEditorConfig(string root, List<LintFinding> findings)
    {
        var editorConfig = Path.Combine(root, ".editorconfig");
        if (!File.Exists(editorConfig))
        {
            return;
        }

        var errorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in DiagnosticSeverity().Matches(File.ReadAllText(editorConfig)))
        {
            if (string.Equals(m.Groups[2].Value, "error", StringComparison.OrdinalIgnoreCase))
            {
                errorIds.Add(m.Groups[1].Value);
            }
        }

        if (errorIds.Count == 0)
        {
            return;
        }

        foreach (var file in EnumerateFiles(root, "*.csproj").Concat(EnumerateFiles(root, "Directory.Build.props")))
        {
            foreach (Match nowarn in NoWarn().Matches(File.ReadAllText(file)))
            {
                var suppressed = nowarn.Groups[1].Value.Split([';', ',', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var id in suppressed.Where(errorIds.Contains))
                {
                    findings.Add(new LintFinding(
                        "dotnet",
                        "error",
                        file,
                        $"NoWarn suppresses {id}, but .editorconfig sets it to error. The two disagree."));
                }
            }
        }
    }

    private static void CheckTsConfigStrict(string root, List<LintFinding> findings)
    {
        foreach (var file in EnumerateFiles(root, "tsconfig.json"))
        {
            if (!StrictTrue().IsMatch(File.ReadAllText(file)))
            {
                findings.Add(new LintFinding("typescript", "warning", file, "\"strict\": true is not set. The lint standard requires strict mode."));
            }
        }
    }

    private static void CheckEslintAndTslint(string root, List<LintFinding> findings)
    {
        foreach (var package in EnumerateFiles(root, "package.json"))
        {
            var dir = Path.GetDirectoryName(package)!;
            var text = File.ReadAllText(package);
            var usesTs = text.Contains("\"typescript\"", StringComparison.Ordinal);
            var hasEslint = Directory.EnumerateFiles(dir, "eslint.config.*").Any()
                            || Directory.EnumerateFiles(dir, ".eslintrc*").Any();

            if (usesTs && !hasEslint)
            {
                findings.Add(new LintFinding("typescript", "error", package, "TypeScript project without an ESLint flat config (eslint.config.mjs)."));
            }

            if (text.Contains("\"tslint\"", StringComparison.Ordinal))
            {
                findings.Add(new LintFinding("typescript", "warning", package, "TSLint is deprecated. Use ESLint with typescript-eslint instead."));
            }
        }

        foreach (var tslint in EnumerateFiles(root, "tslint.json"))
        {
            findings.Add(new LintFinding("typescript", "warning", tslint, "TSLint is deprecated. Use ESLint with typescript-eslint instead."));
        }
    }

    private void CheckDotnetFormat(string root, List<LintFinding> findings)
    {
        var solution = Directory.EnumerateFiles(root, "*.sln*", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (solution is null)
        {
            findings.Add(new LintFinding("dotnet", "info", root, "No solution file at the root, so dotnet format was not run."));
            return;
        }

        var (code, output) = runner.Run("dotnet", ["format", solution, "--verify-no-changes"], root);
        if (code != 0)
        {
            var firstLines = string.Join(" | ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(3));
            findings.Add(new LintFinding("dotnet", "error", solution, $"dotnet format --verify-no-changes failed (exit {code}): {firstLines}"));
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => SkipFolders.Contains(part, StringComparer.OrdinalIgnoreCase)));

    [GeneratedRegex(@"^\s*root\s*=\s*true", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex RootTrue();

    [GeneratedRegex(@"dotnet_diagnostic\.([A-Za-z0-9_]+)\.severity\s*=\s*(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex DiagnosticSeverity();

    [GeneratedRegex(@"<NoWarn>([^<]*)</NoWarn>", RegexOptions.IgnoreCase)]
    private static partial Regex NoWarn();

    [GeneratedRegex("\"strict\"\\s*:\\s*true")]
    private static partial Regex StrictTrue();
}
