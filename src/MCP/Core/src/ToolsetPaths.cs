namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// The three toolset paths a session can use. A session resolves one path at its first tool call and keeps it.
/// The names match the existing <c>preferredIde</c> values, plus <see cref="Standalone" />.
/// </summary>
public static class ToolsetPaths
{
    public const string VisualStudio = "VisualStudio";
    public const string VsCode = "VsCode";
    public const string Standalone = "Standalone";

    /// <summary>Every path, in the order the server lists them.</summary>
    public static readonly IReadOnlyList<string> All = [VisualStudio, VsCode, Standalone];

    /// <summary>True when the value is one of the three paths (case-sensitive, as the config files spell them).</summary>
    public static bool IsKnown(string? value) => value is not null && All.Contains(value);

    /// <summary>The path to use for a configured value: the value when known, otherwise <see cref="Standalone" />.</summary>
    public static string Normalize(string? value) => IsKnown(value) ? value! : Standalone;

    /// <summary>The paths the server allows a repo to choose. An empty allow list means all three.</summary>
    public static IReadOnlyList<string> Allowed(IEnumerable<string>? allowList)
    {
        var list = allowList?.Where(IsKnown).ToList() ?? [];
        return list.Count == 0 ? All : list;
    }
}
