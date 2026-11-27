namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// Routes tool calls by the session's toolset path. The IDE tools (<c>ide_*</c>) already run through the IDE bridge and are never gated here.
/// The helpers listed in <see cref="StandaloneOnlyTools" /> have no IDE equivalent and run only on the Standalone path; on an IDE path
/// they return a refusal that names the path, so the caller sees why instead of a silent fallback.
/// </summary>
public static class ToolsetRouting
{
    /// <summary>Tool names (snake_case) that run only on the Standalone path.</summary>
    public static readonly IReadOnlySet<string> StandaloneOnlyTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "container",
        "wsl",
        "file_edit",
        "model_server_status",
        "model_server_capacity",
    };

    /// <summary>The refusal text for a tool that cannot run on <paramref name="path"/>, or null when the call may proceed.</summary>
    public static string? RefusalFor(string? toolName, string path)
    {
        if (toolName is null || path == ToolsetPaths.Standalone || !StandaloneOnlyTools.Contains(toolName))
        {
            return null;
        }

        return Strings.Format("ToolsetPathRefused", toolName, path);
    }
}
