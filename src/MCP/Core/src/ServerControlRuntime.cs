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
                // Give the notification and any reply in flight a moment, then release the process.
                await Task.Delay(ClosingGrace, stoppingToken);
                lifetime.StopApplication();
                return;
            }
        }
    }

    private static async Task NotifyAsync(ControlState state, CancellationToken ct)
    {
        var server = ControlSession.Server;
        if (server is null)
        {
            return;
        }

        var suffix = string.IsNullOrWhiteSpace(state.Message) ? string.Empty : ": " + state.Message;
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
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException or OperationCanceledException)
        {
            // The client is gone; nothing to tell.
        }
    }
}
