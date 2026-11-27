using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using AiDevHelpers.Bridge;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>
/// Resets a Visual Studio instance that has stopped answering its UI thread. The instance's own watchdog reports the hang through
/// its health reply. The reset itself is a kill, so it needs an explicit confirm: without it, nothing changes.
/// </summary>
[McpServerToolType]
internal sealed class IdeResetTools
{
    [McpServerTool]
    [SafeTool]
    [Description("Reset a Visual Studio instance whose UI thread has stopped answering (health ui_responsive false, or no answer at all). " +
                 "Without confirm it changes nothing and reports what a reset does: ask the user first, then call again with confirm true. " +
                 "A reset closes the instance without saving, so unsaved edits can be lost. Relaunch afterwards with ide_open_solution.")]
    public static async Task<string> IdeResetInstance(
        [Description("Process id of the Visual Studio instance to reset")] int instancePid,
        [Description("Set true only after the user agreed in chat to close the instance without saving")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (VsInstanceRegistry.ListRunning().FirstOrDefault(i => i.Pid == instancePid) is null)
        {
            return Json(new { ok = false, error = Strings.Format("ResetNotFound", instancePid) });
        }

        var health = await IdeInstanceRouter.CallAsync("health", new { }, null, ct, null, instancePid);
        if (IsResponsive(health))
        {
            return Json(new { ok = false, error = Strings.Format("ResetResponsive", instancePid) });
        }

        if (!confirm)
        {
            return Json(new { ok = false, needs_confirmation = true, instance_pid = instancePid, error = Strings.Format("ResetNeedsConfirmation", instancePid) });
        }

        using var process = Process.GetProcessById(instancePid);
        if (!string.Equals(process.ProcessName, "devenv", StringComparison.OrdinalIgnoreCase))
        {
            return Json(new { ok = false, error = Strings.Format("ResetNotDevenv", instancePid, process.ProcessName) });
        }

        process.Kill(entireProcessTree: true);
        StartingInstances.Clear(instancePid);
        return Json(new { ok = true, reset = instancePid, next = Strings.Get("ResetNext") });
    }

    // Responsive means the health reply came back and its UI flag is not false. No reply at all is treated as not responsive.
    private static bool IsResponsive(string health)
    {
        using var doc = JsonDocument.Parse(health);
        if (!doc.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
        {
            return false;
        }

        return !doc.RootElement.TryGetProperty("result", out var result)
            || !result.TryGetProperty("ui_responsive", out var flag)
            || flag.ValueKind != JsonValueKind.False;
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
