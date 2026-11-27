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
internal sealed class RepoOpenService(IElevVerifier verifier, IElevScanner scanner) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<string> OpenSolutions() =>
            VsInstanceRegistry.ListRunning().Select(i => i.SolutionPath).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).ToList();

        var checker = new RepoOpenChecker(
            OpenSolutions,
            verifier,
            RepoSettings.FindRepoRoot,
            root => File.Exists(Path.Combine(root, ".elevated.json")),
            notice => _ = PushAsync(notice));

        // A repo with no .aidevhelpers folder is scanned once per server session, on its own task so a slow scan never delays the tick.
        var setupChecker = new RepoSetupChecker(
            OpenSolutions,
            scanner,
            RepoSettings.FindRepoRoot,
            root => Directory.Exists(Path.Combine(root, RepoSettings.FolderName)),
            notice => _ = PushAsync(notice));

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                setupChecker.CheckOnce(stoppingToken);
                await checker.CheckOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed look is retried on the next tick; the check must never take the server down.
            }
        }
    }

    private static Task PushAsync(RepoVerifyNotice notice) =>
        PushAsync(
            Strings.Format("RepoAllowlistUnapproved", notice.RepoRoot, notice.Unapproved.Count, string.Join(", ", notice.Unapproved)),
            new { repo = notice.RepoRoot, unapproved = notice.Unapproved });

    private static Task PushAsync(RepoElevationNotice notice) =>
        PushAsync(
            Strings.Format("RepoElevationFound", notice.RepoRoot, notice.Projects.Count, string.Join(", ", notice.Projects)),
            new { repo = notice.RepoRoot, elevationProjects = notice.Projects });

    private static async Task PushAsync(string text, object details)
    {
        // The MCP logging feature is deprecated as of spec 2026-07-28 (SEP-2577) with no replacement yet. A client that ignores it
        // can still run the elev --verify check itself. Revisit when the SDK offers a replacement.
#pragma warning disable MCP9005
        // A client that is gone makes its send fail; the session set drops it and the others still get the notice.
        await ControlSession.BroadcastAsync(server => server.SendNotificationAsync(
            NotificationMethods.LoggingMessageNotification,
            new LoggingMessageNotificationParams
            {
                Level = LoggingLevel.Warning,
                Logger = "AiDevHelpers.ScriptHelper",
                Data = System.Text.Json.JsonSerializer.SerializeToElement(new { details, message = text }),
            }));
#pragma warning restore MCP9005
    }
}
