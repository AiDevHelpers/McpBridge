using System.Collections.Concurrent;
using AiDevHelpers.Helpers.TestingHelper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>One solution the monitor watches, with the hot-reload settings the caller reported for it.</summary>
internal sealed record TestWatch(string SolutionPath, bool? IdeHotReload, bool DotnetWatchRunning);

/// <summary>Process-wide testing state: watches, the failure queue, and one TRX monitor per solution.</summary>
internal static class TestingState
{
    public static readonly ConcurrentDictionary<string, TestWatch> Watches = new(StringComparer.OrdinalIgnoreCase);
    public static readonly TestFailureQueue Failures = new();
    public static readonly ConcurrentDictionary<string, TestMonitor> Monitors = new(StringComparer.OrdinalIgnoreCase);

    // The MCP session that started live testing. Set from a tool call, so the monitor can push notifications to that client.
    public static volatile ModelContextProtocol.Server.McpServer? Session;

    public static string ResultsDirectory(string solutionPath)
    {
        var full = Path.GetFullPath(solutionPath);
        var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
        return Path.Combine(dir, "TestResults");
    }
}

/// <summary>
/// Polls the TRX folder of every watched solution while hot reload is active. New failures are queued for
/// test_failures and written as error logs, which the MCP SDK forwards to the client as log notifications (push).
/// </summary>
internal sealed class TestMonitorService(ILogger<TestMonitorService> logger) : BackgroundService
{
    // Sends one MCP log notification per failure to the client that started live testing. Failures still reach the queue and the error log without a session.
    private void Push(TestFailure failure)
    {
        var server = TestingState.Session;
        if (server is null)
        {
            return;
        }

        _ = server.SendNotificationAsync(
            NotificationMethods.LoggingMessageNotification,
            new LoggingMessageNotificationParams
            {
                Level = LoggingLevel.Error,
                Logger = "AiDevHelpers.Testing",
                Data = System.Text.Json.JsonSerializer.SerializeToElement(new { test = failure.TestName, message = failure.Message, trx = failure.Trx }),
            });
    }

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var watch in TestingState.Watches.Values)
            {
                var state = HotReloadPolicy.Resolve(watch.IdeHotReload, watch.DotnetWatchRunning);
                if (!state.Active)
                {
                    continue;
                }

                var monitor = TestingState.Monitors.GetOrAdd(watch.SolutionPath, _ => new TestMonitor(TestingState.Failures));
                foreach (var failure in monitor.Poll(TestingState.ResultsDirectory(watch.SolutionPath)))
                {
                    logger.LogError("Test failed after hot reload: {Test}. {Message}", failure.TestName, failure.Message);
                    Push(failure);
                }
            }
        }
    }
}
