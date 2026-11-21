using AiDevHelpers.Helpers.LogHelper.Models;

namespace AiDevHelpers.Helpers.ErrorHelper;

/// <summary>One error, warning or notice parsed from build output. Severity reuses LogHelper's LogSeverity.</summary>
public sealed record Diagnostic(
    LogSeverity Severity,
    string Code,
    DiagnosticSource Source,
    string Message,
    string? File,
    int? Line,
    int? Column,
    string? Project)
{
    /// <summary>Identity used by snapshots: the same diagnostic at the same place has the same key.</summary>
    public string Key => $"{Source}|{Code}|{File}|{Line}|{Column}|{Message}|{Project}";
}
