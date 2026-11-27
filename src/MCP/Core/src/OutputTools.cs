using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Bridge;
using AiDevHelpers.Helpers.OutputHelper;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Named output channels in Visual Studio or VS Code. Writes go through the IDE bridge (outputWrite).</summary>
[McpServerToolType]
internal sealed class OutputTools
{
    private static readonly OutputService Service = new(new BridgeOutput());

    [McpServerTool]
    [SafeTool]
    [Description("Store which IDE a named output channel writes to, in .claude/aidevhelpers.json next to the .sln.")]
    public static string OutputChannelSet(
        [Description("Full path of the .sln")] string solutionPath,
        [Description("Channel name, e.g. Build")] string channel,
        [Description("VisualStudio or VsCode")] string ide)
    {
        try
        {
            OutputService.SetChannelIde(solutionPath, channel, ide);
            return Json(new { ok = true, channel, ide });
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [SafeTool]
    [Description("Write text to a named output channel. The IDE is the ide argument, or the one stored for the channel. " +
                 "Visual Studio and VS Code channels can be written; they cannot be read back through this tool.")]
    public static async Task<string> OutputWrite(
        [Description("Full path of the .sln")] string solutionPath,
        [Description("Channel name")] string channel,
        [Description("Text to write")] string text,
        [Description("Optional: VisualStudio or VsCode. Overrides the stored IDE.")] string? ide = null,
        CancellationToken ct = default)
    {
        try
        {
            return await Service.WriteAsync(solutionPath, channel, text, ide, ct);
        }
        catch (ArgumentException ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    /// <summary>Sends outputWrite to the chosen IDE through the same router the other ide_ tools use.</summary>
    private sealed class BridgeOutput : IOutputBridge
    {
        public Task<string> CallAsync(string action, object args, string? solutionPath, string ide, CancellationToken ct) =>
            IdeInstanceRouter.CallAsync(action, args, solutionPath, ct, ide: ide);
    }
}
