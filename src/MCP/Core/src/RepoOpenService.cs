using AiDevHelpers.Bridge;
using AiDevHelpers.MCP.Core.Tools;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// The "repo opened in the IDE" trigger for the allowlist check: every few seconds it looks at the open IDE solutions and, for a
/// newly opened repo, asks the broker which allowlisted files this user has not approved and tells the client.
/// </summary>
internal sealed class RepoOpenService(IElevVerifier verifier) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var checker = new RepoOpenChecker(
            () => VsInstanceRegistry.ListRunning().Select(i => i.SolutionPath).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).ToList(),
            verifier,
            RepoSettings.FindRepoRoot,
            root => File.Exists(Path.Combine(root, ".elevated.json")),
            notice => _ = PushAsync(notice));

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await checker.CheckOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed look is retried on the next tick; the check must never take the server down.
            }
        }
    }

    private static async Task PushAsync(RepoVerifyNotice notice)
    {
        var server = ControlSession.Server;
        if (server is null)
        {
            return;
        }

        var text = Strings.Format("RepoAllowlistUnapproved", notice.RepoRoot, notice.Unapproved.Count, string.Join(", ", notice.Unapproved));

        // The MCP logging feature is deprecated as of spec 2026-07-28 (SEP-2577) with no replacement yet. A client that ignores it
        // can still run the elev --verify check itself. Revisit when the SDK offers a replacement.
#pragma warning disable MCP9005
        try
        {
            await server.SendNotificationAsync(
                NotificationMethods.LoggingMessageNotification,
                new LoggingMessageNotificationParams
                {
                    Level = LoggingLevel.Warning,
                    Logger = "AiDevHelpers.ScriptHelper",
                    Data = System.Text.Json.JsonSerializer.SerializeToElement(new { repo = notice.RepoRoot, unapproved = notice.Unapproved, message = text }),
                });
        }
#pragma warning restore MCP9005
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException or OperationCanceledException)
        {
            // The client is gone; nothing to tell.
        }
    }
}
