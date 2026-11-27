using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.MCP.Core.Services;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Pause, close and resume every aidevtools server on this machine for an update. These tools work in every state.</summary>
[McpServerToolType]
internal sealed class ServerControlTools
{
    [McpServerTool]
    [SafeTool]
    [Description("Set the control state for every aidevtools server on this machine. state: paused (tool calls are refused and each chat is told), " +
                 "closing (servers exit so an update can replace their files), or running (resume). The update order is paused, closing, update, running.")]
    public static string ServerControlSet(
        [Description("paused, closing or running")] string state,
        [Description("Optional: why, shown to each chat")] string? message = null)
    {
        try
        {
            var written = ServerControl.Write(state, message);
            return Json(new { ok = true, state = written.State, message = written.Message, updated_utc = written.UpdatedUtc });
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [SafeTool]
    [Description("Read the control state shared by every aidevtools server on this machine.")]
    public static string ServerControlStatus()
    {
        var state = ServerControl.Read();
        return Json(new { ok = true, state = state.State, message = state.Message, updated_utc = state.UpdatedUtc, path = ServerControl.StatePath });
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
