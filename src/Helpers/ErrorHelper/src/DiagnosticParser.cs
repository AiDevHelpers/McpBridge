using System.Text.RegularExpressions;
using AiDevHelpers.Helpers.LogHelper.Models;

namespace AiDevHelpers.Helpers.ErrorHelper;

/// <summary>Parses MSBuild, compiler, analyzer and NuGet restore output into diagnostics.</summary>
public static partial class DiagnosticParser
{
    public static IReadOnlyList<Diagnostic> Parse(string output)
    {
        var result = new List<Diagnostic>();
        foreach (var raw in output.Split('\n'))
        {
            var match = LineRegex().Match(raw.TrimEnd('\r'));
            if (!match.Success)
            {
                continue;
            }

            var code = match.Groups["code"].Value;
            var file = match.Groups["file"].Success ? match.Groups["file"].Value.Trim() : null;
            var line = match.Groups["line"].Success ? int.Parse(match.Groups["line"].Value) : (int?)null;
            var column = match.Groups["col"].Success ? int.Parse(match.Groups["col"].Value) : (int?)null;
            var project = match.Groups["proj"].Success ? match.Groups["proj"].Value.Trim() : null;

            result.Add(new Diagnostic(
                Severity: SeverityOf(match.Groups["sev"].Value),
                Code: code,
                Source: Classify(code),
                Message: match.Groups["msg"].Value.Trim(),
                File: string.IsNullOrEmpty(file) ? null : file,
                Line: line,
                Column: column,
                Project: string.IsNullOrEmpty(project) ? null : project));
        }

        return result;
    }

    /// <summary>Classifies a code by prefix: CS compiler, CA/IDE/SA/RS analyzers, NU NuGet, anything else Other.</summary>
    public static DiagnosticSource Classify(string code)
    {
        var prefix = new string(code.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        return prefix switch
        {
            "CS" => DiagnosticSource.Compiler,
            "CA" or "IDE" or "SA" or "RS" => DiagnosticSource.CodeAnalysis,
            "NU" => DiagnosticSource.NuGet,
            _ => DiagnosticSource.Other,
        };
    }

    private static LogSeverity SeverityOf(string word) => word.ToLowerInvariant() switch
    {
        "error" => LogSeverity.Error,
        "warning" => LogSeverity.Warning,
        _ => LogSeverity.Information,
    };

    [GeneratedRegex(
        @"^\s*(?:(?<file>.+?)(?:\((?<line>\d+),(?<col>\d+)\))?\s*:\s+)?(?<sev>error|warning|info)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)\s*(?:\[(?<proj>[^\]]+)\])?\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LineRegex();
}
