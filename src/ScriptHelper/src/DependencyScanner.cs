using System.Text.RegularExpressions;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// Paths are the file dependencies found (as written, with a leading $PSScriptRoot removed);
/// Unresolved holds dependencies the scanner cannot turn into a file, so approval fails closed.
/// </summary>
public sealed record DependencyScan(IReadOnlyList<string> Paths, IReadOnlyList<string> Unresolved);

/// <summary>
/// Finds dot-sourced scripts and Import-Module paths in a PowerShell script so they can be pinned.
/// Modules imported by name (installed modules) are not files in the repo and are not pinned.
/// </summary>
public static class DependencyScanner
{
    private static readonly Regex DotSource = new(
        @"^\s*\.\s+(?<p>""[^""]*""|'[^']*'|\S+)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex ImportModule = new(
        @"^\s*Import-Module\s+(?<p>""[^""]*""|'[^']*'|\S+)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static DependencyScan Scan(string scriptText)
    {
        var paths = new List<string>();
        var unresolved = new List<string>();

        foreach (Match m in DotSource.Matches(scriptText))
        {
            Add(m.Groups["p"].Value, requirePathLike: false, paths, unresolved);
        }

        foreach (Match m in ImportModule.Matches(scriptText))
        {
            Add(m.Groups["p"].Value, requirePathLike: true, paths, unresolved);
        }

        return new DependencyScan(paths, unresolved);
    }

    private static void Add(string raw, bool requirePathLike, List<string> paths, List<string> unresolved)
    {
        var value = raw.Trim('"', '\'');

        if (value.StartsWith("$PSScriptRoot\\", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("$PSScriptRoot/", StringComparison.OrdinalIgnoreCase))
        {
            value = value["$PSScriptRoot\\".Length..];
        }

        if (requirePathLike && !IsPathLike(value))
        {
            return;
        }

        if (value.Contains('$', StringComparison.Ordinal) || value.Contains('`', StringComparison.Ordinal))
        {
            unresolved.Add(raw);
            return;
        }

        paths.Add(value);
    }

    private static bool IsPathLike(string value) =>
        value.Contains('\\', StringComparison.Ordinal)
        || value.Contains('/', StringComparison.Ordinal)
        || value.EndsWith(".psm1", StringComparison.OrdinalIgnoreCase)
        || value.EndsWith(".psd1", StringComparison.OrdinalIgnoreCase)
        || value.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
}
