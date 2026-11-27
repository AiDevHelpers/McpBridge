using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>What a tool call gets while the server is not running. Pure, so it is tested without a server.</summary>
public static class ServerControlGate
{
    /// <summary>These tools work in every state, so the state can be read and set from a paused server.</summary>
    public static readonly string[] AlwaysAllowed = ["server_control_set", "server_control_status"];

    /// <summary>The refusal text for a tool call in this state, or null when the call may run.</summary>
    public static string? Refusal(string? toolName, ControlState state)
    {
        if (state.IsRunning || (toolName is not null && AlwaysAllowed.Contains(toolName, StringComparer.Ordinal)))
        {
            return null;
        }

        var suffix = string.IsNullOrWhiteSpace(state.Message) ? string.Empty : ": " + state.Message;
        return Strings.Format(state.State == ServerControl.Closing ? "ClosingRefusal" : "PausedRefusal", suffix);
    }
}

/// <summary>The MCP session of this server process, remembered from the last tool call so a state change can be pushed to its client.</summary>
internal static class ControlSession
{
    public static volatile McpServer? Server;
}

/// <summary>
/// Watches the shared control state. On a change it tells this process's client (a log notification), and on a switch to
/// <c>closing</c> it stops the process after a short pause so the update can replace its files. A server that starts while
/// the state is already paused or closing refuses calls but keeps running.
/// </summary>
internal sealed class ServerControlService(IHostApplicationLifetime lifetime) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ClosingGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan QuiesceLimit = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var last = ServerControl.Read().State;
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var current = ServerControl.Read();
            if (current.State == last)
            {
                continue;
            }

            last = current.State;
            await NotifyAsync(current, stoppingToken);

            if (current.State == ServerControl.Closing)
            {
                // Each IDE instance finishes its running step and stops taking actions first. Retry until all confirm or the state
                // changes; stop only when every instance has confirmed.
                var stop = false;
                while (ServerControl.Read().State == ServerControl.Closing)
                {
                    if (await QuiesceInstancesAsync(stoppingToken))
                    {
                        stop = true;
                        break;
                    }

                    await Task.Delay(Interval, stoppingToken);
                }

                if (!stop)
                {
                    continue;
                }

                // Give the notification and any reply in flight a moment, then release the process.
                await Task.Delay(ClosingGrace, stoppingToken);
                lifetime.StopApplication();
                return;
            }
        }
    }

    /// <summary>
    /// Asks every running IDE instance to finish its step and stop taking actions (its <c>quiesce</c> action). True only when each one
    /// confirmed. An instance that cannot be reached counts as not confirmed, so the server keeps running and the closing state stays.
    /// </summary>
    internal static async Task<bool> QuiesceInstancesAsync(CancellationToken ct)
    {
        var confirmed = true;
        foreach (var instance in AiDevHelpers.Bridge.VsInstanceRegistry.ListRunning())
        {
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(QuiesceLimit);
                var json = await AiDevHelpers.Bridge.IdeInstanceRouter.CallAsync("quiesce", new { }, solutionPath: null, limit.Token, instancePid: instance.Pid);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var ok = doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.ValueKind == System.Text.Json.JsonValueKind.True;
                confirmed &= ok;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                confirmed = false;
            }
        }

        return confirmed;
    }

    private static async Task NotifyAsync(ControlState state, CancellationToken ct)
    {
        var server = ControlSession.Server;
        if (server is null)
        {
            return;
        }

        var suffix = string.IsNullOrWhiteSpace(state.Message) ? string.Empty : ": " + state.Message;

        // The MCP logging feature is deprecated as of spec 2026-07-28 (SEP-2577) with no replacement yet. A client that ignores the
        // notice still sees the state from server_control_status and from the refused tool calls. Revisit when the SDK offers a replacement.
#pragma warning disable MCP9005
        var (level, text) = state.State switch
        {
            ServerControl.Paused => (LoggingLevel.Warning, Strings.Format("PausedNotice", suffix)),
            ServerControl.Closing => (LoggingLevel.Warning, Strings.Format("ClosingNotice", suffix)),
            _ => (LoggingLevel.Info, Strings.Get("ResumedNotice")),
        };

        try
        {
            await server.SendNotificationAsync(
                NotificationMethods.LoggingMessageNotification,
                new LoggingMessageNotificationParams
                {
                    Level = level,
                    Logger = "AiDevHelpers.Control",
                    Data = System.Text.Json.JsonSerializer.SerializeToElement(new { state = state.State, message = text }),
                },
                cancellationToken: ct);
        }
#pragma warning restore MCP9005
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException or OperationCanceledException)
        {
            // The client is gone; nothing to tell.
        }
    }
}
