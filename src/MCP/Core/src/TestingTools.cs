using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Helpers.TestingHelper;
using AiDevHelpers.MCP.Core.Services;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Watch a solution for hot-reload test failures, drain them, and run coverage.</summary>
[McpServerToolType]
internal sealed class TestingTools
{
    [McpServerTool]
    [Description("Start watching a solution. While hot reload is active the server polls its TestResults folder and reports new failures " +
                 "(log notification and test_failures). ideHotReload is on, off or unknown; dotnetWatchRunning is true when dotnet watch runs.")]
    public static string TestWatch(
        [Description("Full path of the .sln or its folder")] string solutionPath,
        [Description("IDE hot reload setting: on, off or unknown")] string ideHotReload = "unknown",
        [Description("True when dotnet watch is running for the solution")] bool dotnetWatchRunning = false)
    {
        bool? ide = ideHotReload.ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            "unknown" => null,
            _ => throw new ArgumentException("ideHotReload must be on, off or unknown."),
        };
        var full = Path.GetFullPath(solutionPath);
        TestingState.Watches[full] = new TestWatch(full, ide, dotnetWatchRunning);
        return Json(new { ok = true, watching = full, state = HotReloadState(ide, dotnetWatchRunning) });
    }

    [McpServerTool]
    [Description("Stop watching a solution.")]
    public static string TestUnwatch([Description("Full path of the .sln or its folder")] string solutionPath)
    {
        var full = Path.GetFullPath(solutionPath);
        return Json(new { ok = TestingState.Watches.TryRemove(full, out _), watching = full });
    }

    [McpServerTool]
    [Description("Return test failures queued since the last call, then clear the queue.")]
    public static string TestFailures()
    {
        var items = TestingState.Failures.Drain()
            .Select(f => new { test = f.TestName, message = f.Message, trx = f.Trx, at = f.Timestamp });
        return Json(new { ok = true, count = items.Count(), items });
    }

    [McpServerTool]
    [Description("Run code coverage for a solution. provider is Coverlet or VisualStudio. " +
                 "Default: Coverlet when the repo references it, otherwise VisualStudio (dotnet-coverage CLI).")]
    public static async Task<string> TestCoverage(
        [Description("Full path of the .sln or its folder")] string solutionPath,
        [Description("Optional: Coverlet or VisualStudio")] string? provider = null,
        CancellationToken ct = default)
    {
        var full = Path.GetFullPath(solutionPath);
        var solutionFile = Directory.Exists(full) ? full : full;
        var root = Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;

        CoverageProvider chosen;
        if (string.IsNullOrWhiteSpace(provider))
        {
            var projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                .Select(File.ReadAllText);
            chosen = CoverageProviderSelector.Select(projects);
        }
        else if (!Enum.TryParse<CoverageProvider>(provider, ignoreCase: true, out chosen))
        {
            return Json(new { ok = false, error = $"Unknown provider '{provider}'. Use Coverlet or VisualStudio." });
        }

        var service = new CoverageService(new ProcessTestCommandRunner(), Path.Combine(TestingState.ResultsDirectory(full), "coverage"));
        var result = await service.RunAsync(solutionFile, chosen, bridgeAvailable: false, ct);
        return Json(new
        {
            ok = result.Ok,
            provider = chosen.ToString(),
            tool = result.Tool,
            reportPath = result.ReportPath,
            lineRate = result.LineRate,
            error = result.Error,
        });
    }

    private static object HotReloadState(bool? ide, bool dotnetWatch)
    {
        var state = HotReloadPolicy.Resolve(ide, dotnetWatch);
        return new { source = state.Source.ToString(), active = state.Active };
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
