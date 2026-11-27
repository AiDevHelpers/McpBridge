using AiDevHelpers.Bridge;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>One reply from an IDE bridge: did it work, what did it return, which instance answered.</summary>
internal sealed record BridgeReply(bool Ok, JsonElement? Result, string? Error, int? Pid, string? Ide);

/// <summary>The seam between the debugger control service and a running IDE (a Visual Studio or VS Code window).</summary>
internal interface IIdeBridge
{
    /// <summary>Runs <paramref name="action" /> in the IDE of the given kind ("VisualStudio" or "VsCode").</summary>
    Task<BridgeReply> CallAsync(string ide, string action, object args, CancellationToken ct);
}

/// <summary>Production bridge: <see cref="IdeInstanceRouter" /> over the IDE's named pipe.</summary>
internal sealed class IdeInstanceBridge : IIdeBridge
{
    public async Task<BridgeReply> CallAsync(string ide, string action, object args, CancellationToken ct)
    {
        string json = await IdeInstanceRouter.CallAsync(action, args, solutionPath: null, ct, ide);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        bool ok = root.TryGetProperty("ok", out JsonElement okProp) && okProp.ValueKind == JsonValueKind.True;
        JsonElement? result = ok && root.TryGetProperty("result", out JsonElement r) && r.ValueKind != JsonValueKind.Null ? r.Clone() : null;
        string? error = root.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        int? pid = root.TryGetProperty("instance_pid", out JsonElement p) && p.TryGetInt32(out int pidValue) ? pidValue : null;
        string? answeredBy = root.TryGetProperty("ide", out JsonElement i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
        return new BridgeReply(ok, result, error, pid, answeredBy);
    }
}

/// <summary>
/// Sends the global dialog settings (IdeTools) to each running IDE instance the first time the server sees it, and again when a value
/// changes. The bridge applies them with its configure action and reports a change as settingsChanged.
/// </summary>
internal sealed class BridgeSettingsService(IOptions<IdeToolsOptions> options) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SendLimit = TimeSpan.FromSeconds(10);
    private readonly Dictionary<int, string> _sent = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var autoDismiss = options.Value.AutoDismissDialogs;
            var seconds = options.Value.ActionTimeoutSeconds;
            var current = $"{autoDismiss}|{seconds}";

            foreach (var instance in AiDevHelpers.Bridge.VsInstanceRegistry.ListRunning())
            {
                if (_sent.TryGetValue(instance.Pid, out var last) && last == current)
                {
                    continue;
                }

                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    limit.CancelAfter(SendLimit);
                    await AiDevHelpers.Bridge.IdeInstanceRouter.CallAsync(
                        "configure",
                        new { autoDismissDialogs = autoDismiss, actionTimeoutSeconds = seconds },
                        solutionPath: null,
                        limit.Token,
                        instancePid: instance.Pid);
                    _sent[instance.Pid] = current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not sent: the next tick tries this instance again.
                }
            }
        }
    }
}
