using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Common.Processes;
using AiDevHelpers.Helpers.TestingHelper;
using AiDevHelpers.MCP.Core.Services;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Watch a solution for hot-reload test failures, drain them, and run coverage.</summary>
[McpServerToolType]
internal sealed class TestingTools
{
    [McpServerTool]
    [SafeTool]
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
            _ => throw new ArgumentException(Strings.Get("HotReloadInvalid")),
        };
        var full = Path.GetFullPath(solutionPath);
        TestingState.Watches[full] = new TestWatch(full, ide, dotnetWatchRunning);
        return Json(new { ok = true, watching = full, state = HotReloadState(ide, dotnetWatchRunning) });
    }

    [McpServerTool]
    [SafeTool]
    [Description("Stop watching a solution.")]
    public static string TestUnwatch([Description("Full path of the .sln or its folder")] string solutionPath)
    {
        var full = Path.GetFullPath(solutionPath);
        return Json(new { ok = TestingState.Watches.TryRemove(full, out _), watching = full });
    }

    [McpServerTool]
    [SafeTool]
    [Description("List the test projects a change can affect, and the dotnet test command for each. A changed file affects its project and every project that references it. " +
                 "With no changedFiles, the files git reports as changed (working tree and untracked) are used. Nothing is run.")]
    public static async Task<string> TestAffected(
        [Description("Repo root")] string root,
        [Description("Optional: changed files, relative to the root or absolute")] string[]? changedFiles = null)
    {
        try
        {
            var files = changedFiles ?? await GitChangedFilesAsync(root);
            var projects = AffectedTests.Find(root, files);
            return Json(new
            {
                ok = true,
                changed = files.Count(),
                count = projects.Count,
                projects = projects.Select(p => new { project = p, command = $"dotnet test \"{p}\"" }),
            });
        }
        catch (DirectoryNotFoundException ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private static async Task<IReadOnlyList<string>> GitChangedFilesAsync(string root)
    {
        var tracked = await GitOutputAsync(root, ["diff", "--name-only", "HEAD"]);
        var untracked = await GitOutputAsync(root, ["ls-files", "--others", "--exclude-standard"]);
        return (tracked + "\n" + untracked)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("warning:", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();
    }

    private static async Task<string> GitOutputAsync(string root, string[] arguments)
    {
        var result = await CommandRunner.RunAsync(new CommandRequest { FileName = "git", Arguments = arguments, WorkingDirectory = root });
        return result.StdOut + result.StdErr;
    }

    [McpServerTool]
    [SafeTool]
    [Description("Return test failures queued since the last call, then clear the queue.")]
    public static string TestFailures()
    {
        var items = TestingState.Failures.Drain()
            .Select(f => new { test = f.TestName, message = f.Message, trx = f.Trx, at = f.Timestamp });
        return Json(new { ok = true, count = items.Count(), items });
    }

    [McpServerTool]
    [SafeTool]
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
            return Json(new { ok = false, error = Strings.Format("UnknownProvider", provider) });
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

    [McpServerTool]
    [SafeTool]
    [Description("Start live testing for a solution with dotnet watch test. Works in Visual Studio Community, which has no Live Unit Testing. " +
                 "Each run writes TestResults/live.trx; failures are reported through test_failures and the error log.")]
    public static string LiveTestStart(McpServer server, [Description("Full path of the .sln or its folder")] string solutionPath)
    {
        TestingState.Session = server;
        var full = Path.GetFullPath(solutionPath);
        var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
        var results = Path.Combine(dir, "TestResults");
        Directory.CreateDirectory(results);

        lock (LiveRuns)
        {
            if (LiveRuns.TryGetValue(full, out var running) && running.Any(p => !p.HasExited))
            {
                return Json(new { ok = true, alreadyRunning = true, pids = running.Select(p => p.Id), trx = results });
            }

            // dotnet watch needs a project, not a solution, so one watch runs per test project.
            var projects = AffectedTests.AllTestProjects(dir);
            if (projects.Count == 0)
            {
                return Json(new { ok = false, error = Strings.Format("NoTestProjects", dir) });
            }

            var started = new List<System.Diagnostics.Process>();
            foreach (var project in projects)
            {
                var name = Path.GetFileNameWithoutExtension(project);
                var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                    CreateNoWindow = true,

                    // The server's own stdin and stdout are the MCP pipe, so the watch gets pipes of its own.
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var arg in new[] { "watch", "--project", project, "test", "--logger", $"trx;LogFileName=live.{name}.trx", "--results-directory", results })
                {
                    psi.ArgumentList.Add(arg);
                }

                var process = System.Diagnostics.Process.Start(psi);
                if (process is null)
                {
                    StopAll(started);
                    return Json(new { ok = false, error = Strings.Get("WatchNotStarted") });
                }

                process.OutputDataReceived += (_, _) => { };
                process.ErrorDataReceived += (_, _) => { };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                started.Add(process);
            }

            // A watch that cannot start (no SDK, bad project) exits within moments; report it instead of claiming success.
            Thread.Sleep(LiveStartCheck);
            var exited = started.FirstOrDefault(p => p.HasExited);
            if (exited is not null)
            {
                var code = exited.ExitCode;
                StopAll(started);
                return Json(new { ok = false, error = Strings.Format("WatchExitedEarly", code) });
            }

            LiveRuns[full] = started;
            TestingState.Watches[full] = new TestWatch(full, IdeHotReload: true, DotnetWatchRunning: true);
            return Json(new { ok = true, pids = started.Select(p => p.Id), trx = results });
        }
    }

    private static readonly TimeSpan LiveStartCheck = TimeSpan.FromSeconds(4);

    private static void StopAll(IEnumerable<System.Diagnostics.Process> processes)
    {
        foreach (var process in processes)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    [McpServerTool]
    [SafeTool]
    [Description("Stop live testing for a solution started by live_test_start.")]
    public static string LiveTestStop([Description("Full path of the .sln or its folder")] string solutionPath)
    {
        var full = Path.GetFullPath(solutionPath);
        TestingState.Watches.TryRemove(full, out _);
        lock (LiveRuns)
        {
            if (!LiveRuns.TryGetValue(full, out var processes))
            {
                return Json(new { ok = false, error = Strings.Get("NoLiveTesting") });
            }

            LiveRuns.Remove(full);
            StopAll(processes);
        }

        return Json(new { ok = true, stopped = full });
    }

    private static readonly Dictionary<string, List<System.Diagnostics.Process>> LiveRuns = new(StringComparer.OrdinalIgnoreCase);

    private static object HotReloadState(bool? ide, bool dotnetWatch)
    {
        var state = HotReloadPolicy.Resolve(ide, dotnetWatch);
        return new { source = state.Source.ToString(), active = state.Active };
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
