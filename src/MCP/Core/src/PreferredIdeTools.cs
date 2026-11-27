using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Bridge;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Stores the preferred IDE for a solution in .claude/aidevhelpers.json, used only to break a routing tie.</summary>
[McpServerToolType]
internal sealed class PreferredIdeTools
{
    [McpServerTool]
    [SafeTool]
    [Description("Store the preferred IDE for a solution in .claude/aidevhelpers.json next to the .sln. " +
                 "It is used only when two IDE instances have the same solution open. An explicit ide or instancePid argument still wins.")]
    public static string IdeSetPreferred(
        [Description("Full path of the .sln")] string solutionPath,
        [Description("VisualStudio or VsCode")] string ide)
    {
        try
        {
            RepoConfig.SetPreferredIde(solutionPath, ide);
            return Json(new { ok = true, solution_path = solutionPath, preferred_ide = ide, config_path = RepoConfig.PathFor(solutionPath) });
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [SafeTool]
    [Description("Read the preferred IDE stored for a solution, or none.")]
    public static string IdeGetPreferred(
        [Description("Full path of the .sln")] string solutionPath) =>
        Json(new { ok = true, preferred_ide = RepoConfig.PreferredIde(solutionPath) });

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
