using System.Text.Json;
using AiDevHelpers.Bridge;

namespace AiDevHelpers.Tray;

public enum ActionOutcome
{
    Closed,
    Denied,
    Refused,
    Failed,
}

public sealed record ActionResult(ActionOutcome Outcome, string Message);

/// <summary>
/// Closes an IDE instance from the Instances page. The rules, in order: Visual Studio only; refused while the debugger is not in design
/// mode; the person must say yes in the tray dialog; then the bridge's closeSolution runs with save first and confirm, on that
/// instance's pid only. Nothing is sent before the yes.
/// </summary>
public sealed class InstanceActions(
    Func<string, object, int, CancellationToken, Task<string>> send,
    Func<VsInstance, Task<bool>> confirm)
{
    public async Task<ActionResult> CloseAsync(VsInstance instance, CancellationToken ct)
    {
        if (instance.Ide != "VisualStudio")
        {
            return new ActionResult(ActionOutcome.Refused, Strings.Get("CloseVsCodeNotSupported"));
        }

        var debugState = await send("debugState", new { }, instance.Pid, ct);
        var mode = DebuggerMode(debugState);
        if (mode != "dbgDesignMode")
        {
            return new ActionResult(ActionOutcome.Refused, Strings.Get("CloseRefusedDebugging"));
        }

        if (!await confirm(instance))
        {
            return new ActionResult(ActionOutcome.Denied, Strings.Get("CloseDenied"));
        }

        var reply = await send("closeSolution", new { saveFirst = true, confirm = true }, instance.Pid, ct);
        return Reply(reply) is { } error
            ? new ActionResult(ActionOutcome.Failed, Strings.Format("CloseFailed", error))
            : new ActionResult(ActionOutcome.Closed, Strings.Get("CloseDone"));
    }

    /// <summary>The debugger mode from a debugState reply, or null when the reply does not carry one.</summary>
    private static string? DebuggerMode(string reply)
    {
        try
        {
            using var doc = JsonDocument.Parse(reply);
            return doc.RootElement.TryGetProperty("result", out var result)
                && result.TryGetProperty("debugger_mode", out var mode)
                && mode.ValueKind == JsonValueKind.String
                    ? mode.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Null when the bridge applied the action; otherwise its error text.</summary>
    private static string? Reply(string reply)
    {
        try
        {
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                return null;
            }

            return root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : "no reply";
        }
        catch (JsonException)
        {
            return "unreadable reply";
        }
    }
}
