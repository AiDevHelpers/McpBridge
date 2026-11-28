using System.ComponentModel;
using AiDevHelpers.Bridge;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Completion candidates from the Visual Studio bridge. Read-only.</summary>
[McpServerToolType]
internal sealed class CompletionTools
{
    [McpServerTool]
    [SafeTool]
    [Description("Completion candidates at a position in a source file, from Visual Studio's open workspace (unsaved editor text included). Line and column are 1-based. Returns display text, filter and sort text, inline description and tags, up to limit items (default 50). Read-only.")]
    public static Task<string> IdeCompleteAt(
        [Description("Full path of the source file")] string file,
        [Description("1-based line")] int line,
        [Description("Optional: 1-based column (default 1)")] int? column = null,
        [Description("Optional: most items to return (default 50)")] int? limit = null,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("completeAt", new { file, line, column, limit }, solutionPath, ct, ide, instancePid);
}
