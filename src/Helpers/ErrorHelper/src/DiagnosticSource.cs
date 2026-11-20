namespace AiDevHelpers.Helpers.ErrorHelper;

/// <summary>Where a diagnostic came from, classified from its code prefix.</summary>
public enum DiagnosticSource
{
    /// <summary>Compiler diagnostics (CS codes).</summary>
    Compiler,

    /// <summary>Roslyn analyzers (CA, IDE, SA, RS codes).</summary>
    CodeAnalysis,

    /// <summary>NuGet restore diagnostics (NU codes).</summary>
    NuGet,

    /// <summary>Anything else, such as SDK notices (NETSDK codes).</summary>
    Other,
}
