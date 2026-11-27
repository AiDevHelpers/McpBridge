using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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

/// <summary>
/// The MCP sessions of this server process, each remembered from its last tool call, so a state change reaches every client that is
/// connected. Over HTTP one process serves several sessions; over stdio there is one.
/// </summary>
internal static class ControlSession
{
    private static readonly ConcurrentDictionary<string, McpServer> ServersBySession = new(StringComparer.Ordinal);

    /// <summary>Remembers a session's server. Called from every tool call.</summary>
    public static void Remember(McpServer server)
    {
        if (!string.IsNullOrEmpty(server.SessionId))
        {
            ServersBySession[server.SessionId] = server;
        }
    }

    /// <summary>Runs <paramref name="send"/> for every remembered session. A session whose send fails is forgotten, since it is gone.</summary>
    public static async Task BroadcastAsync(Func<McpServer, Task> send)
    {
        foreach (var (id, server) in ServersBySession)
        {
            try
            {
                await send(server).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException or OperationCanceledException)
            {
                ServersBySession.TryRemove(id, out _);
            }
        }
    }
}

/// <summary>
/// Which MCP session planned each confirm token. Over HTTP one process serves several sessions, so a token planned in one chat must
/// not be confirmed from another. Tokens are learned from tool results that carry a "token" field.
/// </summary>
public static class PlanOwnership
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Owners = new(StringComparer.Ordinal);

    public static void Record(string token, string sessionId) => Owners[token] = sessionId;

    /// <summary>True unless the token was planned by a different session. An unknown token is left to the tool to refuse.</summary>
    public static bool MayUse(string token, string sessionId) =>
        !Owners.TryGetValue(token, out var owner) || string.Equals(owner, sessionId, StringComparison.Ordinal);

    /// <summary>Records the "token" field of a tool result's text, if it has one, as planned by this session.</summary>
    public static void RecordFrom(string resultText, string sessionId)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(resultText);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("token", out var token)
                && token.ValueKind == System.Text.Json.JsonValueKind.String
                && !string.IsNullOrEmpty(token.GetString()))
            {
                Record(token.GetString()!, sessionId);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON, so no token to learn.
        }
    }
}

/// <summary>A request, through the tray, for the person's yes on one call to a dangerous tool. The reply must echo the nonce and hash.</summary>
public sealed record ApprovalRequest(string Nonce, string ToolName, string ArgumentsHash, DateTime ExpiresUtc);

/// <summary>The tray's answer. Approved is true only when the person said yes for this call.</summary>
public sealed record ApprovalReply(string Nonce, string ArgumentsHash, bool Approved);

/// <summary>The channel to the person. A null reply, or no reply in time, means no approval.</summary>
public interface IApprovalChannel
{
    Task<ApprovalReply?> RequestAsync(ApprovalRequest request, CancellationToken ct);
}

/// <summary>
/// Asks for a dangerous call's approval and accepts only an exact yes: the nonce and the argument hash must match this request, and the
/// request must not have expired. Every other outcome is a refusal (fail closed). Pure except for the channel and the clock.
/// </summary>
public static class ApprovalGate
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    public static ApprovalRequest CreateRequest(string toolName, string argumentsJson, DateTime nowUtc) =>
        new(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), toolName, HashOf(toolName, argumentsJson), nowUtc + Limit);

    public static string HashOf(string toolName, string argumentsJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(toolName + "\n" + argumentsJson)));

    /// <summary>True only for a yes that echoes this request's nonce and argument hash, before it expires.</summary>
    public static bool Accepts(ApprovalRequest request, ApprovalReply? reply, DateTime nowUtc)
    {
        if (reply is null || !reply.Approved || nowUtc > request.ExpiresUtc)
        {
            return false;
        }

        return SameText(reply.Nonce, request.Nonce) && SameText(reply.ArgumentsHash, request.ArgumentsHash);
    }

    /// <summary>Asks the channel and returns whether the call may run. No channel, a timeout, an error or a refusal all mean no.</summary>
    public static async Task<bool> ApproveAsync(IApprovalChannel? channel, string toolName, string argumentsJson, Func<DateTime> now, CancellationToken ct)
    {
        if (channel is null)
        {
            return false;
        }

        var request = CreateRequest(toolName, argumentsJson, now());
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Limit);
        try
        {
            var reply = await channel.RequestAsync(request, limit.Token).ConfigureAwait(false);
            return Accepts(request, reply, now());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static bool SameText(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }
}

/// <summary>Which MCP tools are dangerous, read from their [DangerousTool] attribute. A tool's name is its method name in snake_case.</summary>
public static class ToolClassification
{
    private static readonly Lazy<HashSet<string>> DangerousNames = new(Collect, isThreadSafe: true);

    public static bool IsDangerous(string? toolName) => toolName is not null && DangerousNames.Value.Contains(toolName);

    private static HashSet<string> Collect()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in typeof(ToolClassification).Assembly.GetTypes())
        {
            if (type.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolTypeAttribute), false).Length == 0)
            {
                continue;
            }

            foreach (var method in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                                                   | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
            {
                if (method.GetCustomAttributes(typeof(DangerousToolAttribute), false).Length > 0
                    && method.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false).Length > 0)
                {
                    names.Add(Regex.Replace(method.Name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant());
                }
            }
        }

        return names;
    }
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
            await ControlSession.BroadcastAsync(server => server.SendNotificationAsync(
                NotificationMethods.LoggingMessageNotification,
                new LoggingMessageNotificationParams
                {
                    Level = level,
                    Logger = "AiDevHelpers.Control",
                    Data = System.Text.Json.JsonSerializer.SerializeToElement(new { state = state.State, message = text }),
                },
                cancellationToken: ct));
        }
#pragma warning restore MCP9005
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException or OperationCanceledException)
        {
            // The client is gone; nothing to tell.
        }
    }
}
