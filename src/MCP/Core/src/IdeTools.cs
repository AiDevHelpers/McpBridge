using AiDevHelpers.Bridge;
using System.ComponentModel;
using System.Text.Json;

using ModelContextProtocol.Server;

using Microsoft.Extensions.Options;
using AiDevHelpers.MCP.Core.Services;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>
///     Common IDE actions for any running IDE instance. Every result names the IDE and instance that answered
///     (<c>ide</c>, <c>instance_pid</c>, <c>solution_path</c>), so the caller always knows what it acted on.
/// </summary>
[McpServerToolType]
internal sealed class IdeTools(IOptions<IdeToolsOptions> options, IElevRunner? elevRunner = null)
{
    private static readonly TimeSpan BridgeReadyTimeout = TimeSpan.FromSeconds(60);

    // True when the instance's pipe accepts a connection, which is how the bridge says it is ready.
    private static bool BridgeAnswers(VsInstance instance)
    {
        try
        {
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", instance.PipeName, System.IO.Pipes.PipeDirection.InOut);
            pipe.Connect(1000);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or System.IO.IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Resolves the solution a test-result call refers to: the given path, or the only open instance's solution.
    private static string? ResolveSolution(string? solutionPath)
    {
        if (!string.IsNullOrWhiteSpace(solutionPath))
        {
            return solutionPath;
        }

        var instances = VsInstanceRegistry.ListRunning();
        return instances.Count == 1 ? instances[0].SolutionPath : null;
    }

    // The test run mode in effect for a solution: the repo's .aidevtools/settings.json wins over the global setting.
    private string EffectiveTestRunMode(string? solutionPath) =>
        IdeTestRunService.NormalizeMode(RepoSettings.Resolve(options.Value, ResolveSolution(solutionPath)).Options.IdeTestRunMode);

    [McpServerTool]
    [Description("List running IDE instances (currently Visual Studio) with their process id and the solution each has open.")]
    public string IdeListInstances() => IdeInstanceRouter.ListInstances();

    [McpServerTool]
    [Description("Set the startup project of the solution in an IDE instance. " +
                 "Pass solutionPath to choose the instance when several are open.")]
    public Task<string> IdeSetStartupProject(
        [Description("Project name to make the startup project")] string project,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("setStartupProject", new { project }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Open a file in an IDE instance and move the caret to an optional 1-based line. Pass solutionPath when several instances are open.")]
    public Task<string> IdeOpenDocument(
        [Description("Full path of the file")] string file,
        [Description("Optional: 1-based line to go to")] int? line = null,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("openDocument", new { file, line }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Open a solution in Visual Studio by launching devenv with the .sln path. Works even when the IDE bridge is not answering.")]
    public async Task<string> IdeOpenSolution(
        [Description("Full path of the .sln")] string solutionPath)
    {
        if (!File.Exists(solutionPath))
        {
            return System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = Strings.Get("SolutionFileMissing") });
        }

        // Reuse an instance that already has this solution open, so a second devenv is not started for it.
        var existing = VsInstanceRegistry.ListRunning().FirstOrDefault(i =>
            !string.IsNullOrEmpty(i.SolutionPath)
            && string.Equals(Path.GetFullPath(i.SolutionPath), Path.GetFullPath(solutionPath), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return System.Text.Json.JsonSerializer.Serialize(new { ok = true, opened = solutionPath, already_running = true, instance_pid = existing.Pid });
        }

        // With an approved allow-all for the repo and an elevation policy, the launch goes through the broker's run channel,
        // so no UAC prompt. Otherwise the normal launch (with its own UAC prompt when elevation is needed) is used.
        var brokered = await TryBrokerLaunchAsync(solutionPath);
        if (brokered.Error is not null)
        {
            return System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = brokered.Error });
        }

        if (!brokered.Used)
        {
            var error = DevenvLauncher.Open(solutionPath, out var startedPid);
            if (error is null && startedPid is { } launched)
            {
                StartingInstances.Write(launched, solutionPath);
            }

            if (error is not null)
            {
                return System.Text.Json.JsonSerializer.Serialize(new { ok = false, error });
            }
        }

        // Return only once the new instance has registered and its bridge answers, so a follow-up call does not race it.
        var deadline = DateTime.UtcNow + BridgeReadyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var started = VsInstanceRegistry.ListRunning().FirstOrDefault(i =>
                !string.IsNullOrEmpty(i.SolutionPath)
                && string.Equals(Path.GetFullPath(i.SolutionPath), Path.GetFullPath(solutionPath), StringComparison.OrdinalIgnoreCase));
            if (started is not null && BridgeAnswers(started))
            {
                StartingInstances.Clear(started.Pid);
                return System.Text.Json.JsonSerializer.Serialize(new { ok = true, opened = solutionPath, already_running = false, instance_pid = started.Pid, bridge_ready = true });
            }

            Thread.Sleep(500);
        }

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            ok = false,
            opened = solutionPath,
            bridge_ready = false,
            error = Strings.Get("OpenBridgeNotReady"),
        });
    }

    // Repo-relative script that starts devenv. It runs through the broker, so it is covered by the repo's allow-all approval.
    private const string OpenScript = ".aidevtools/open-solution.ps1";

    private async Task<(bool Used, string? Error)> TryBrokerLaunchAsync(string solutionPath)
    {
        if (elevRunner is null)
        {
            return (false, null);
        }

        var full = Path.GetFullPath(solutionPath);
        var repo = AiDevHelpers.Common.RepoSettingsFile.FindRepoRootFromFolder(Path.GetDirectoryName(full));
        if (repo is null || !AiDevHelpers.Common.ElevationPolicy.Decide(full).Elevated || !AllowAllApproved(repo))
        {
            return (false, null);
        }

        var result = await elevRunner.RunAsync(repo, OpenScript, [full], CancellationToken.None);
        return result.ExitCode == 0
            ? (true, null)
            : (true, Strings.Format("ElevatedOpenFailed", result.ExitCode, result.Output));
    }

    // True when this repo's settings file still has the hash the person approved allow-all for.
    private static bool AllowAllApproved(string repo)
    {
        var user = AiDevHelpers.Common.RepoSettingsFile.UserFilePath(repo);
        if (!File.Exists(user))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(user));
            if (!doc.RootElement.TryGetProperty("allowAll", out var entry))
            {
                return false;
            }

            var recorded = entry.GetProperty("settingsHash").GetString();
            var settings = AiDevHelpers.Common.RepoSettingsFile.SettingsPath(repo);
            return File.Exists(settings)
                && string.Equals(recorded, AiDevHelpers.Common.RepoSettingsFile.Hash(settings), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    [McpServerTool]
    [Description("Start debugging the solution in an IDE instance. Pass solutionPath when several instances are open.")]
    public Task<string> IdeStartDebugging(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        DebugElevationGuard.Check(solutionPath, ide, instancePid) is { } refusal
            ? Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = refusal }))
            : IdeInstanceRouter.CallAsync("startDebugging", new { }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Stop debugging in an IDE instance. Pass solutionPath when several instances are open.")]
    public Task<string> IdeStopDebugging(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("stopDebugging", new { }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Run all tests in the solution. The server setting IdeTestRunMode picks the method: 'TestExplorer' " +
                 "(default) triggers Test Explorer in the IDE; 'DotnetTest' runs dotnet test with a TRX file. " +
                 "Pass solutionPath when several instances are open. Use IdeGetTestResults for the outcome. " +
                 "Pass project (a .csproj path, relative to the solution or absolute) to run only that project; it always uses dotnet test, " +
                 "so it can run while a whole-solution Test Explorer run is going.")]
    public Task<string> IdeRunTests(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        [Description("Optional: one test project (.csproj) to run instead of the whole solution. Uses dotnet test.")] string? project = null,
        CancellationToken ct = default)
    {
        if (project is not null)
        {
            var solution = ResolveSolution(solutionPath);
            return solution is null
                ? Task.FromResult(JsonSerializer.Serialize(new { ok = false, error = Strings.Get("PassSolutionPathNoInstance") }))
                : IdeTestRunService.RunDotnetTestAsync(solution, ct, project);
        }

        if (EffectiveTestRunMode(solutionPath) == IdeTestRunService.DotnetTestMode)
        {
            var solution = ResolveSolution(solutionPath);
            return solution is null
                ? Task.FromResult(JsonSerializer.Serialize(new { ok = false, error = Strings.Get("PassSolutionPathNoInstance") }))
                : IdeTestRunService.RunDotnetTestAsync(solution, ct);
        }

        return IdeInstanceRouter.CallAsync("runTests", new { }, solutionPath, ct, ide, instancePid);
    }

    [McpServerTool]
    [Description("Format every C# file of the solution with the IDE's Format Document command, saving each one. Opens each file.")]
    public Task<string> IdeFormatSolution(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("formatSolution", new { }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Get the outcome of the newest test run for the solution. In 'TestExplorer' mode (default) it reads the IDE's Tests output pane: " +
                 "counts, failing test names and failure_details with each failure's message. In 'DotnetTest' mode it reads the run's TRX file, " +
                 "which also covers TRX files written by other test runs in the solution's TestResults folder.")]
    public async Task<string> IdeGetTestResults(
        [Description("Optional: full path of the solution to read results for")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open (TestExplorer mode).")] string? ide = null,
        [Description("Optional: process id of the instance to read (TestExplorer mode).")] int? instancePid = null,
        CancellationToken ct = default)
    {
        if (EffectiveTestRunMode(solutionPath) != IdeTestRunService.DotnetTestMode)
        {
            return await IdeGetTestSummary(solutionPath, ide, instancePid, ct);
        }

        var solution = ResolveSolution(solutionPath);
        return solution is null
            ? JsonSerializer.Serialize(new { ok = false, error = Strings.Get("PassSolutionPathNoInstance") })
            : IdeTestRunService.GetLatestResult(solution);
    }

    [McpServerTool]
    [Description("Get the counts of the newest finished Test Explorer run, read from the Visual Studio Tests output pane. " +
                 "Works in TestExplorer mode, where no TRX is written. failures lists the tests the runner marked [FAIL] in that run; it is empty when the pane has no such lines.")]
    public async Task<string> IdeGetTestSummary(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var raw = await IdeInstanceRouter.CallAsync(
            "searchOutput",
            new { pattern = "Test run finished: \\d+ Tests|\\[FAIL\\]|\\[xUnit\\.net", pane = "Tests" },
            solutionPath, ct, ide, instancePid);
        return TestSummaryParser.ParseLatest(raw);
    }

    [McpServerTool]
    [Description("Search the lines of an IDE output pane for a regex pattern. Returns the matching lines.")]
    public Task<string> IdeSearchOutput(
        [Description("Regex pattern to match against each output line")] string pattern,
        [Description("Output pane name, e.g. 'Build' or 'Debug'")] string pane = "Build",
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("searchOutput", new { pattern, pane }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Clear an output pane in an IDE instance.")]
    public Task<string> IdeClearOutput(
        [Description("Output pane name, e.g. 'Build' or 'Debug'")] string pane = "Build",
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("clearOutput", new { pane }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Format the active document in an IDE instance. Pass solutionPath when several instances are open.")]
    public Task<string> IdeFormatDocument(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("formatDocument", new { }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Build the solution in an IDE instance and report whether it succeeded. " +
                 "Pass solutionPath when several instances are open. Use IdeGetBuildDiagnostics for the errors.")]
    public Task<string> IdeBuild(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("build", new { }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Read build errors and warnings from an IDE instance's Error List (the last build's results).")]
    public Task<string> IdeGetBuildDiagnostics(
        [Description("'error', 'warning', or 'all' (default)")] string severity = "all",
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("getBuildDiagnostics", new { severity }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Run Visual Studio's Code Cleanup on the active document of an IDE instance.")]
    public Task<string> IdeCodeCleanup(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("codeCleanup", new { }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Read the last lines of an IDE output pane, e.g. 'Build' or 'Debug'. For a filtered, deduplicated summary, use LogQuery with a vsout: channel.")]
    public Task<string> IdeGetOutput(
        [Description("Output pane name, e.g. 'Build' or 'Debug'")] string pane = "Build",
        [Description("Maximum number of trailing lines to return")] int maxLines = 200,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("getOutput", new { pane, maxLines }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Read local variables in the current stack frame of an IDE instance. The debugger must be stopped at a breakpoint.")]
    public Task<string> IdeGetLocals(
        [Description("Maximum number of variables to return")] int maxItems = 100,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("getLocals", new { maxItems }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Check the bridge health of an IDE instance: whether its listener is running, when it started, " +
                 "when it last handled a request, how many requests it has handled, and its solution. " +
                 "Answered even while the IDE is busy.")]
    public Task<string> IdeBridgeHealth(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("health", new { }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("List the solution build configurations (e.g. Debug, Release) of an IDE instance and the active one.")]
    public Task<string> IdeListConfigurations(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("listConfigurations", new { }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Set the active build configuration (e.g. Debug or Release) of an IDE instance's solution.")]
    public Task<string> IdeSetConfiguration(
        [Description("Configuration name, e.g. 'Debug' or 'Release'")] string configuration,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("setConfiguration", new { configuration }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Clean the solution in an IDE instance using the active configuration.")]
    public Task<string> IdeClean(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("clean", new { }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Publish the startup project of an IDE instance with the IDE's publish command, using the active configuration.")]
    public Task<string> IdePublish(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("publish", new { }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Kill a process that an IDE instance started for clean or publish. Refuses any other process id.")]
    public Task<string> IdeKillProcess(
        [Description("Process id returned by IdeClean or IdePublish")] int pid,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("killProcess", new { pid }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Launch a Visual Studio instance with a solution open. Reuses an instance that already has it open. " +
                 "Waits for the instance's bridge to come up. Launches elevated (UAC prompt) only for a solution with a project the repo lists as needing " +
                 "administrator rights, or when this user's mode for the repo is always; a running non-elevated instance of such a solution is not reused " +
                 "unless relaunchElevated is true.")]
    public Task<string> IdeLaunchInstance(
        [Description("Full path of the .sln file to open")] string solutionPath,
        [Description("Only after the user agreed: when the solution needs administrator rights and its open instance is not elevated, save and close that instance and start it elevated (one UAC prompt)")] bool relaunchElevated = false,
        CancellationToken ct = default) =>
        IdeInstanceLifecycle.LaunchAsync(solutionPath, ct, relaunchElevated);

    [McpServerTool]
    [Description("Save all documents and then kill a Visual Studio instance. Refuses, killing nothing, if any document is still unsaved.")]
    public Task<string> IdeKillInstance(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        CancellationToken ct = default) =>
        IdeInstanceLifecycle.KillAsync(solutionPath, ct);

    [McpServerTool]
    [Description("Set a breakpoint at a file and line in an IDE instance.")]
    public Task<string> IdeSetBreakpoint(
        [Description("Full path of the source file")] string file,
        [Description("1-based line number")] int line,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("setBreakpoint", new { file, line }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Remove breakpoints at a file and line in an IDE instance.")]
    public Task<string> IdeRemoveBreakpoint(
        [Description("Full path of the source file")] string file,
        [Description("1-based line number")] int line,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("removeBreakpoint", new { file, line }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("List breakpoints set in an IDE instance.")]
    public Task<string> IdeListBreakpoints(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("listBreakpoints", new { }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Step the debugger in an IDE instance: 'over', 'into', or 'out'. The debugger must be stopped at a breakpoint.")]
    public Task<string> IdeStep(
        [Description("'over', 'into', or 'out'")] string mode,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("step", new { mode }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Get the debugger mode (design, run, or break) and the current function in an IDE instance.")]
    public Task<string> IdeGetDebugState(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("debugState", new { }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Evaluate an expression in the current stack frame of an IDE instance. The debugger must be stopped at a breakpoint.")]
    public Task<string> IdeEvaluate(
        [Description("C# expression to evaluate, e.g. 'items.Count'")] string expression,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("evaluate", new { expression }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Add a watch expression to an IDE instance's watch list. Watches are kept by the extension, not the Watch window.")]
    public Task<string> IdeAddWatch(
        [Description("Expression to watch")] string expression,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("addWatch", new { expression }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Remove a watch expression from an IDE instance's watch list.")]
    public Task<string> IdeRemoveWatch(
        [Description("Expression to remove")] string expression,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("removeWatch", new { expression }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("List an IDE instance's watch expressions with their current values (when stopped at a breakpoint).")]
    public Task<string> IdeListWatches(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("listWatches", new { }, solutionPath, ct, ide, instancePid);
}
