namespace AiDevHelpers.Helpers.ErrorHelper;

/// <summary>Optional narrowing by code (case-insensitive) and by source. Null or empty means no restriction.</summary>
public sealed record DiagnosticFilter(
    IReadOnlyCollection<string>? Codes = null,
    IReadOnlyCollection<DiagnosticSource>? Sources = null);
