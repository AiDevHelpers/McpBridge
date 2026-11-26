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
    /// <summary>Events read by default (plan 4.3): run results, status and errors. Build and break errors come through here.</summary>
    private static readonly string[] DefaultEventNames =
    {
        "buildDone", "enterBreakMode", "enterRunMode", "solutionOpened", "solutionClosing", "dropped",
    };

    private readonly IdeSolutionOpener _opener = new(
        new DevenvSolutionLauncher(elevRunner),
        VsInstanceRegistry.FindBySolution,
        IdeSolutionOpener.PipeAnswers,
        (delay, ct) => Task.Delay(delay, ct),
        () => DateTime.UtcNow,
        TimeSpan.FromSeconds(60));

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
    public Task<string> IdeOpenSolution(
        [Description("Full path of the .sln")] string solutionPath,
        CancellationToken ct = default) => _opener.OpenAsync(solutionPath, ct);

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

        return RunTestsAndSummarizeAsync(solutionPath, ide, instancePid, ct);
    }

    [McpServerTool]
    [Description("Format every C# file of the solution with the IDE's Format Document command, saving each one. Opens each file.")]
    public Task<string> IdeFormatSolution(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("formatSolution", new { }, solutionPath, ct, ide, instancePid);

    // Test Explorer refuses a run while it is still busy. Wait once on this thread and try again; the IDE never sleeps.
    /// <summary>
    /// Starts the run, then waits for its summary (plan 3.3). The summary read before the run is the baseline: a finished run
    /// is one whose summary differs from it, so an earlier run is not mistaken for this one. Returns status "running" after the
    /// wait limit, with the call to read later.
    /// </summary>
    private async Task<string> RunTestsAndSummarizeAsync(string? solutionPath, string? ide, int? instancePid, CancellationToken ct)
    {
        var baseline = await IdeGetTestSummary(solutionPath, ide, instancePid, ct);
        var started = await RunTestExplorerWithRetryAsync(solutionPath, ide, instancePid, ct);
        using (var startedDoc = JsonDocument.Parse(started))
        {
            if (!startedDoc.RootElement.TryGetProperty("ok", out var startedOk) || !startedOk.GetBoolean())
            {
                return started;
            }
        }

        var waitSeconds = DefaultTestWaitSeconds;
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            var now = await IdeGetTestSummary(solutionPath, ide, instancePid, ct);
            using var doc = JsonDocument.Parse(now);
            if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean() && now != baseline)
            {
                return JsonSerializer.Serialize(new { status = "finished", summary = TestSummaryCapper.Cap(doc.RootElement) });
            }
        }

        return JsonSerializer.Serialize(new { ok = true, status = "running", wait_seconds = waitSeconds, hint = Strings.Get("RunStillRunningHint") });
    }

    private const int DefaultTestWaitSeconds = 120;

    private static async Task<string> RunTestExplorerWithRetryAsync(string? solutionPath, string? ide, int? instancePid, CancellationToken ct)
    {
        var first = await IdeInstanceRouter.CallAsync("runTests", new { }, solutionPath, ct, ide, instancePid);
        if (!IsTestExplorerRefusal(first))
        {
            return first;
        }

        await Task.Delay(3000, ct);
        var second = await IdeInstanceRouter.CallAsync("runTests", new { }, solutionPath, ct, ide, instancePid);
        if (!IsTestExplorerRefusal(second))
        {
            return second;
        }

        using var doc = JsonDocument.Parse(second);
        var hresult = doc.RootElement.TryGetProperty("result", out var result) && result.TryGetProperty("hresult", out var h) ? h.GetInt32() : 0;
        return JsonSerializer.Serialize(new { ok = false, attempts = 2, error = Strings.Format("TestExplorerRefusedTwice", hresult) });
    }

    private static bool IsTestExplorerRefusal(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
            && doc.RootElement.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("refused", out var refused) && refused.ValueKind == JsonValueKind.True;
    }

    [McpServerTool]
    [Description("Go to the definition of the symbol at file:line:column in an IDE instance. Places the caret, then runs the IDE command. Moves the IDE's active document and caret. Returns the IDE state, not the target location. Visual Studio only.")]
    public Task<string> IdeGoToDefinition(
        [Description("Full path of the file")] string file,
        [Description("1-based line")] int line,
        [Description("Optional: 1-based column of the symbol")] int column = 1,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("goToDefinition", new { file, line, column }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Find all references to the symbol at file:line:column in an IDE instance. Places the caret, then runs the IDE command. The results open in the IDE's Find All References window, which this tool does not read.")]
    public Task<string> IdeFindAllReferences(
        [Description("Full path of the file")] string file,
        [Description("1-based line")] int line,
        [Description("Optional: 1-based column of the symbol")] int column = 1,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("findAllReferences", new { file, line, column }, solutionPath, ct, ide, instancePid);

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
    public async Task<string> IdeBuild(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        // The bridge build blocks until the build finishes. The reply is a summary: status, and on failure the errors (capped).
        var raw = await IdeInstanceRouter.CallAsync("build", new { }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        using var reply = JsonDocument.Parse(raw);
        if (!reply.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            return raw;
        }

        var result = reply.RootElement.GetProperty("result").Clone();
        var succeeded = result.TryGetProperty("succeeded", out var s) && s.ValueKind == JsonValueKind.True;
        JsonElement? errors = null;
        using var diagnostics = succeeded
            ? null
            : JsonDocument.Parse(await IdeInstanceRouter.CallAsync("getBuildDiagnostics", new { severity = "error" }, solutionPath, ct, ide, instancePid).ConfigureAwait(false));
        if (diagnostics is not null && diagnostics.RootElement.TryGetProperty("ok", out var dok) && dok.GetBoolean())
        {
            errors = diagnostics.RootElement.GetProperty("result").GetProperty("diagnostics").Clone();
        }

        var summary = BuildSummary.Build(result, errors);
        return JsonSerializer.Serialize(new { ok = true, pid = reply.RootElement.GetProperty("pid").GetInt32(), summary });
    }

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

    [McpServerTool]
    [Description("List the processes the IDE's debugger can attach to (local processes), with pid and name. Read-only.")]
    public Task<string> IdeListProcesses(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("listProcesses", new { }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("List the processes the IDE's debugger is currently attached to. Read-only.")]
    public Task<string> IdeListDebuggedProcesses(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("listDebuggedProcesses", new { }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("List the threads of the debugged process with their ids, and the current thread. Empty unless the debugger is in break mode. Read-only.")]
    public Task<string> IdeListThreads(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("listThreads", new { }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("List the debugger's exception settings, grouped as the debugger groups them, with break-when-thrown and break-when-user-unhandled. Read-only.")]
    public Task<string> IdeListExceptions(
        [Description("Optional: only this exception group, for example 'Common Language Runtime Exceptions'. The full list is large; name a group when you can.")] string? group = null,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("listExceptions", new { group }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("List the stack frames of the current thread (index, function, return type). Empty unless the debugger is in break mode. Read-only.")]
    public Task<string> IdeListStackFrames(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("listStackFrames", new { }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Set the condition, hit-count type, or hit count of the breakpoint(s) at a file and line. Re-adds the breakpoint, so hit counts reset. Without confirm it only returns the change. Show the plan to the user and get their yes before setting confirm to true.")]
    public Task<string> IdeSetBreakpointCondition(
        [Description("Full path of the source file")] string file,
        [Description("1-based line number")] int line,
        [Description("Optional: condition expression, for example 'count > 10'")] string? condition = null,
        [Description("Optional: always, equal, multiple, or greaterOrEqual")] string? hitCountType = null,
        [Description("Optional: hit count for the hit-count type")] int? hitCount = null,
        [Description("Set true to apply the change. Default false returns the plan only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("setBreakpointCondition", new { file, line, condition, hitCountType, hitCount, confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Enable the breakpoint(s) at a file and line. Without confirm it only returns the change. Show the plan to the user and get their yes before setting confirm to true.")]
    public Task<string> IdeEnableBreakpoint(
        [Description("Full path of the source file")] string file,
        [Description("1-based line number")] int line,
        [Description("Set true to apply the change. Default false returns the plan only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("enableBreakpoint", new { file, line, confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Disable the breakpoint(s) at a file and line. Without confirm it only returns the change. Show the plan to the user and get their yes before setting confirm to true.")]
    public Task<string> IdeDisableBreakpoint(
        [Description("Full path of the source file")] string file,
        [Description("1-based line number")] int line,
        [Description("Set true to apply the change. Default false returns the plan only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("disableBreakpoint", new { file, line, confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Attach the debugger to one local process by pid. Without confirm it only returns the target. Show the plan to the user and get their yes before setting confirm to true.")]
    public Task<string> IdeAttachProcess(
        [Description("Process id from ide_list_processes")] int pid,
        [Description("Set true to apply the change. Default false returns the plan only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("attachProcess", new { pid, confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Detach the debugger from every attached process. Without confirm it only lists what would be detached. Show the plan to the user and get their yes before setting confirm to true.")]
    public Task<string> IdeDetachAll(
        [Description("Set true to apply the change. Default false returns the plan only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("detachAll", new { confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Search the text of files: the solution, one project, or the open documents. Managed search over files on disk; read-only. Capped at 500 matches.")]
    public Task<string> IdeFindInFiles(
        [Description("Text to find, or a regular expression when useRegex is true")] string pattern,
        [Description("Match case. Default false.")] bool matchCase = false,
        [Description("Treat pattern as a regular expression. Default false.")] bool useRegex = false,
        [Description("solution, project, or openDocuments. Default solution.")] string scope = "solution",
        [Description("Project name, required when scope is project")] string? project = null,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("findInFiles", new { pattern, matchCase, useRegex, scope, project }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Replace text in files (solution, project, or open documents). Without confirm it only counts matches per file and writes nothing. Open documents with unsaved changes are skipped. Show the plan to the user and get their yes before setting confirm to true.")]
    public Task<string> IdeReplaceInFiles(
        [Description("Text to find, or a regular expression when useRegex is true")] string pattern,
        [Description("Replacement text. With useRegex, $1 etc. refer to groups.")] string replacement,
        [Description("Match case. Default false.")] bool matchCase = false,
        [Description("Treat pattern as a regular expression. Default false.")] bool useRegex = false,
        [Description("solution, project, or openDocuments. Default solution.")] string scope = "solution",
        [Description("Project name, required when scope is project")] string? project = null,
        [Description("Set true to apply the change. Default false returns the plan only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("replaceInFiles", new { pattern, replacement, matchCase, useRegex, scope, project, confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("List the open documents with saved state and read-only flag. Read-only.")]
    public Task<string> IdeListDocuments(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("listDocuments", new { }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Save an open document. Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true.")]
    public Task<string> IdeSaveDocument(
        [Description("Full path of the open document")] string file,
        [Description("Set true to apply the change. Default false returns the plan only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("saveDocument", new { file, confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Close an open document. A document with unsaved changes is refused unless discardChanges is also true. Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true.")]
    public Task<string> IdeCloseDocument(
        [Description("Full path of the open document")] string file,
        [Description("Discard unsaved changes when closing. Default false.")] bool discardChanges = false,
        [Description("Set true to apply the change. Default false returns the plan only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("closeDocument", new { file, discardChanges, confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Apply several range edits in one IDE undo group, so they undo as one step. Each edit gives start and end line and column (1-based) and new text. Without confirm it lists the edits. Show the plan to the user and get their yes before setting confirm to true.")]
    public Task<string> IdeUndoGroup(
        [Description("Name of the undo group shown in the IDE")] string name,
        [Description("Array of {file, startLine, startColumn, endLine, endColumn, newText}")] object[] edits,
        [Description("Set true to apply the change. Default false returns the plan only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("undoGroup", new { name, edits, confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("List the code elements (namespaces, types, members) of one file with line ranges. Read-only.")]
    public Task<string> IdeListCodeElements(
        [Description("Full path of the source file")] string file,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("listCodeElements", new { file }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Read named properties of a project (for example TargetFramework, OutputType). Unknown names return null. Read-only.")]
    public Task<string> IdeProjectProperties(
        [Description("Project name, as shown in Solution Explorer")] string project,
        [Description("Property names to read")] string[] names,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("projectProperties", new { project, names }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Add a file to a project: mode fromFile adds an existing file; mode fromNew creates one from a template path. Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true.")]
    public Task<string> IdeAddProjectItem(
        [Description("Project name")] string project,
        [Description("Full path of the file to add or create")] string file,
        [Description("fromFile or fromNew")] string mode,
        [Description("Template path, required for fromNew")] string? template = null,
        [Description("Set true to apply the change. Default false returns the plan only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("addProjectItem", new { project, file, mode, template, confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Remove a file from a project. The file stays on disk. Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true.")]
    public Task<string> IdeRemoveProjectItem(
        [Description("Project name")] string project,
        [Description("Full path of the file to remove from the project")] string file,
        [Description("Set true to apply the change. Default false returns the plan only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("removeProjectItem", new { project, file, confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Close the solution. Refused while documents have unsaved changes unless saveFirst is true. Without confirm it lists the dirty documents and the plan. Show the plan to the user and get their yes before setting confirm to true.")]
    public Task<string> IdeCloseSolution(
        [Description("Save dirty documents before closing. Default false.")] bool saveFirst = false,
        [Description("Set true to apply the change. Default false returns the plan only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("closeSolution", new { saveFirst, confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("List the items in the Visual Studio Task List window (category, text, file, line). Read-only.")]
    public Task<string> IdeListTasks(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("listTasks", new { }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Set the status bar text of the IDE (empty clears it). Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true.")]
    public Task<string> IdeSetStatus(
        [Description("Status text; empty clears it")] string text,
        [Description("Set true to apply the change. Default false returns the plan only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("setStatus", new { text, confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Show an info bar in the IDE's main window with a message and optional button labels. Buttons do not report clicks. Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true.")]
    public Task<string> IdeShowInfoBar(
        [Description("Message text")] string text,
        [Description("Optional: button labels")] string[]? actions = null,
        [Description("Set true to apply the change. Default false returns the plan only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("showInfoBar", new { text, actions = (actions ?? Array.Empty<string>()).Select(label => new { label }), confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Write an entry to the Visual Studio activity log (ActivityLog.xml). Level is Error, Warning, or Information. Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true.")]
    public Task<string> IdeWriteActivityLog(
        [Description("Error, Warning, or Information")] string level,
        [Description("Source name for the entry")] string source,
        [Description("Message text")] string message,
        [Description("Set true to apply the change. Default false returns the plan only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("writeActivityLog", new { level, source, message, confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Read one Visual Studio setting by its key path. Returns found:false when the setting does not exist. Read-only.")]
    public Task<string> IdeGetSetting(
        [Description("Setting key path")] string key,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("getSetting", new { key }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Add a task to the Visual Studio Task List under the bridge's own provider. Category is Error, Warning, or Message. Without confirm it only returns the plan. Show the plan to the user and get their yes before setting confirm to true.")]
    public Task<string> IdeAddTask(
        [Description("Task text")] string text,
        [Description("Optional: full path of the file the task refers to")] string? file = null,
        [Description("Optional: 1-based line in the file")] int? line = null,
        [Description("Error, Warning, or Message. Default Message.")] string category = "Message",
        [Description("Set true to apply the change. Default false returns the plan only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("addTask", new { text, file, line, category, confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Remove every task this bridge added to the Task List. Tasks from other tools are not touched. Without confirm it only reports the count. Show the plan to the user and get their yes before setting confirm to true.")]
    public Task<string> IdeClearTasks(
        [Description("Set true to apply the change. Default false returns the plan only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("clearTasks", new { confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Find every reference to the symbol at a file position, across the solution, using Roslyn. Sees unsaved editor edits. Read-only.")]
    public Task<string> IdeSemanticReferences(
        [Description("Full path of the source file")] string file,
        [Description("1-based line number")] int line,
        [Description("Optional: 1-based column. Default 1.")] int? column = null,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("semanticReferences", new { file, line, column }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Find where the symbol at a file position is declared in source, using Roslyn. Sees unsaved editor edits. Read-only.")]
    public Task<string> IdeSemanticDefinition(
        [Description("Full path of the source file")] string file,
        [Description("1-based line number")] int line,
        [Description("Optional: 1-based column. Default 1.")] int? column = null,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("semanticDefinition", new { file, line, column }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Rename the symbol at a file position everywhere in the solution, using Roslyn. Without confirm it only lists the files that would change. Show the plan to the user and get their yes before setting confirm to true.")]
    public Task<string> IdeRenameSymbol(
        [Description("Full path of the source file")] string file,
        [Description("1-based line number")] int line,
        [Description("New name for the symbol")] string newName,
        [Description("Optional: 1-based column. Default 1.")] int? column = null,
        [Description("Set true to apply the rename. Default false returns the plan only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("renameSymbol", new { file, line, newName, column, confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Replace one C# method using Roslyn, with checks. The method at the position is replaced by newMethodText (one full method declaration). The edit is refused if it adds compile errors, or if the signature changes and any reference (from ide_semantic_references) has no entry in callerEdits. Each caller edit names a reference by file, line and column and gives the text that replaces the invocation (or the bare name for a method group or nameof). A signature change on a method that overrides, implements or is overridden is refused. If the method has a /// doc comment, its param, typeparam and returns tags are updated to the new signature in the comment's own layout; a new parameter needs its text in docs (the tool never invents text), and newSummary replaces the summary when the purpose changed. Roslyn cannot see reflection, strings, XAML or projects outside the solution, so also search the name with ide_find_in_files. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied it runs the tests that call the method (or the affected test projects when none do) with coverage and returns each test's status and the method's line coverage in test_run; pass runTests false to skip. Visual Studio only.")]
    public async Task<string> IdeEditMethod(
        [Description("Full path of the source file")] string file,
        [Description("1-based line inside the method (its name line is best)")] int line,
        [Description("Full replacement text of the method declaration")] string newMethodText,
        [Description("Optional: 1-based column. Default 1.")] int? column = null,
        [Description("Optional: array of {file, line, column, newText} for callers when the signature changes. file, line and column are a reference location from ide_semantic_references.")] object[]? callerEdits = null,
        [Description("Optional: new text for the summary of the method's XML doc comment, when the method's purpose changed.")] string? newSummary = null,
        [Description("Optional: array of {tag, name, text} for doc tags: tag is param, typeparam or returns. Needed for each new parameter or type parameter when the method has a doc comment; the tool never invents text.")] object[]? docs = null,
        [Description("Optional: fields and properties the method needs, added to its type in the same checked change. Array of {name, type, accessibility (required: public, internal, protected, private, protected internal, private protected), backing (auto, field, readonly-field, field-only, expression), setter, required, initializer, attributes, summary}.")] object[]? members = null,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Optional: true rewrites the call sites to match a clean removal or reorder of parameters (matched by name). Adds, renames and retypes are refused: use ide_change_signature. Cannot be combined with callerEdits. Default false.")] bool rewriteCallers = false,
        [Description("Set true to apply the edit. Default false returns the checks only.")] bool confirm = false,
        [Description("Run the tests that call the method, with coverage, after the change is applied. Default true.")] bool runTests = true,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("editMethod", new { file, line, column, newMethodText, callerEdits = callerEdits ?? [], newSummary, docs = docs ?? [], members = members ?? [], rewriteCallers, confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return await MethodTestRunner.AttachAsync(reply, confirm && runTests, ct).ConfigureAwait(false);
    }

    [McpServerTool]
    [Description("Merge the parts of a partial class, struct or record into the part at a line, using Roslyn, with checks. The other parts' members move to the end of that part, their usings the part's file lacks are added, and their declarations are removed; a part that was alone in its file takes the file with it. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Visual Studio only.")]
    public async Task<string> IdeMergePartial(
        [Description("Full path of a file holding one part of the type")] string file,
        [Description("1-based line inside the part to merge into")] int line,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("mergePartial", new { file, line, confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return reply;
    }

    [McpServerTool]
    [Description("Change a file's layout, using Roslyn, with checks. action \"split\" moves the top-level type at line out of a file that holds several types into its own file named after it, beside the original, with the original's usings and namespace. action \"rename\" renames a file that holds exactly one type to that type's name. Refused when the target file exists. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Visual Studio only.")]
    public async Task<string> IdeOrganizeFile(
        [Description("Full path of the source file")] string file,
        [Description("1-based line inside the type (for split); any line for rename")] int line,
        [Description("\"split\" or \"rename\"")] string action,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("organizeFile", new { file, line, action, confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return reply;
    }

    [McpServerTool]
    [Description("Move a method, property or field to another type in the solution, using Roslyn, with checks. toType is the destination type's name (or its full display name when the simple name is ambiguous). The member's text and doc comment move to the end of the destination. References qualified by the source type's name are renamed to the destination; other references need a callerEdits entry each. Refused for an override, implementation or implemented member, and for a move that stops compiling in its new place. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Visual Studio only.")]
    public async Task<string> IdeMoveMember(
        [Description("Full path of the source file")] string file,
        [Description("1-based line inside the member to move")] int line,
        [Description("Optional: 1-based column. Default 1.")] int? column = null,
        [Description("The destination type's name, or its full display name")] string toType = "",
        [Description("Optional: array of {file, line, column, newText} for references that are not qualified by the source type's name.")] object[]? callerEdits = null,
        [Description("Optional: true pulls the member up to its base class (toType is then ignored). The base class must be declared in the solution. Default false.")] bool pullUp = false,
        [Description("Optional: true moves an instance method as a static member: \"static\" is added, unqualified and this. calls in the source type become Dest.M(). Refused when the body uses instance state. Default false.")] bool toStatic = false,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("moveMember", new { file, line, column = column ?? 1, toType, callerEdits = callerEdits ?? [], toStatic, pullUp, confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return reply;
    }

    [McpServerTool]
    [Description("Add Equals, GetHashCode or ToString to the class, struct or record that contains a line, using Roslyn, with checks. kind is \"equals\", \"hashcode\" or \"tostring\"; members names the fields and properties it uses. Refused when the type already declares that member. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Visual Studio only.")]
    public async Task<string> IdeGenerateMembers(
        [Description("Full path of the source file")] string file,
        [Description("1-based line inside the class, struct or record")] int line,
        [Description("\"equals\", \"hashcode\" or \"tostring\"")] string kind,
        [Description("Field and property names the member uses")] string[] members,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("generateMembers", new { file, line, kind, members, confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return reply;
    }

    [McpServerTool]
    [Description("Add a constructor to the class, struct or record that contains a line, using Roslyn, with checks. members names the fields and properties it takes and assigns (in order); each parameter is the member name with the first letter lower-cased. accessibility defaults to public. Refused for a member with no setter, an unknown member, or a constructor that already takes the same parameter types. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Visual Studio only.")]
    public async Task<string> IdeGenerateConstructor(
        [Description("Full path of the source file")] string file,
        [Description("1-based line inside the class, struct or record")] int line,
        [Description("Field and property names to take and assign, in parameter order")] string[] members,
        [Description("Optional: public, internal, protected or private. Default public.")] string? accessibility = null,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("generateConstructor", new { file, line, members, accessibility = accessibility ?? "public", confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return reply;
    }

    [McpServerTool]
    [Description("Change the modifiers and base list of the class, struct or record at a line, using Roslyn, with checks. modifiers replaces the whole set, such as \"public sealed\" or \"internal static partial\"; for a partial type it must include partial and applies to every part. baseList replaces the base class and interfaces of the part at the line, such as [\"Base\", \"IDisposable\"]. Adding abstract or static is refused while the type is used. Narrowing access and other breaking uses are refused by the checks. Attributes, type parameters and constraints are not changed. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Visual Studio only.")]
    public async Task<string> IdeEditType(
        [Description("Full path of the source file")] string file,
        [Description("1-based line inside the type")] int line,
        [Description("Optional: the full new set of modifiers, space separated")] string? modifiers = null,
        [Description("Optional: the new base class and interfaces of the part at the line. An empty array removes them.")] string[]? baseList = null,
        [Description("Optional: attributes to add to the type at the line, such as \"Serializable\"")] string[]? attributesAdd = null,
        [Description("Optional: attributes to remove from the type at the line, by name, such as \"Serializable\"")] string[]? attributesRemove = null,
        [Description("Optional: the new type parameter list, such as \"<T, U>\". An empty string removes it. Partial parts must match.")] string? typeParameters = null,
        [Description("Optional: the new constraint clauses, such as \"where T : class, new()\". An empty string removes them.")] string? constraints = null,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("editType", new { file, line, modifiers, baseList, attributesAdd = attributesAdd ?? [], attributesRemove = attributesRemove ?? [], typeParameters, constraints, confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return reply;
    }

    [McpServerTool]
    [Description("Add stubs to the class, struct or record that contains a line, using Roslyn, with checks. mode \"interface\" stubs every member of its interfaces that nothing implements yet; mode \"override\" overrides the members named in members (virtual or abstract members of its base classes). explicit true writes the interface stubs as explicit implementations. Each stub throws System.NotImplementedException. The stubs go at the end of the type. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Visual Studio only.")]
    public async Task<string> IdeImplementMembers(
        [Description("Full path of the source file")] string file,
        [Description("1-based line inside the class, struct or record")] int line,
        [Description("\"interface\" or \"override\"")] string mode,
        [Description("For mode override: the member names to override")] string[]? members = null,
        [Description("Optional: true writes interface stubs as explicit implementations. Default false.")] bool @explicit = false,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("implementMembers", new { file, line, mode, members = members ?? [], @explicit, confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return reply;
    }

    [McpServerTool]
    [Description("Add a new type (class, struct, record, interface, enum or delegate) in its own file beside anchorFile, using Roslyn, with checks. declaration is the type's full C# text without a doc comment or usings. The file takes the anchor's project and namespace unless namespace is given. usings, summary and attributes are written around the declaration in the file's own layout. Refused when a type of the same name exists or a file is already at the path. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Visual Studio only.")]
    public async Task<string> IdeAddType(
        [Description("Full path of an existing source file in the target project; the new file goes beside it")] string anchorFile,
        [Description("The type's full C# text, such as \"public sealed class Widget { }\"")] string declaration,
        [Description("Optional: usings the new file needs, such as \"System.Text\"")] string[]? usings = null,
        [Description("Optional: namespace for the new type. Default is the anchor file's namespace.")] string? @namespace = null,
        [Description("Optional: summary text for the type's doc comment")] string? summary = null,
        [Description("Optional: attributes for the type, such as \"Serializable\" or \"[Serializable]\"")] string[]? attributes = null,
        [Description("Optional: also add a test class beside the type in the test project that refers to anchorFile's project. It asserts failure until written. Default false.")] bool withTests = false,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("addType", new { anchorFile, declaration, usings = usings ?? [], namespaceName = @namespace, summary, attributes = attributes ?? [], withTests, confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return reply;
    }

    [McpServerTool]
    [Description("Add a method, property, event or indexer to an interface, using Roslyn, with checks. declaration is its full C# text. Every type that implements the interface would lack the member, so the reply lists each implementer and the change is refused while there are any. Without confirm it only returns the checks. Visual Studio only.")]
    public async Task<string> IdeAddInterfaceMember(
        [Description("Full path of the source file")] string file,
        [Description("1-based line inside the interface")] int line,
        [Description("The member's full C# text, such as \"void Save(int id);\"")] string declaration,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("addInterfaceMember", new { file, line, declaration, confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return reply;
    }

    [McpServerTool]
    [Description("Add a member to an enum, using Roslyn, with checks. name is the member's name and value an optional constant expression. The member goes after the last member, in the enum's own comma layout. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Visual Studio only.")]
    public async Task<string> IdeAddEnumMember(
        [Description("Full path of the source file")] string file,
        [Description("1-based line inside the enum")] int line,
        [Description("The new member's name")] string name,
        [Description("Optional: the member's constant value, such as 4 or 1 << 3")] string? value = null,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("addEnumMember", new { file, line, name, value, confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return reply;
    }

    [McpServerTool]
    [Description("Change the value of the enum member at a line, using Roslyn, with checks. A [Flags] enum's values are bits, and an enum that looks persisted (it has attributes, or is public) may be stored by number, so those refuse a value change unless allowValueChange is true. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Visual Studio only.")]
    public async Task<string> IdeEditEnumMember(
        [Description("Full path of the source file")] string file,
        [Description("1-based line of the enum member")] int line,
        [Description("The new constant value, such as 4 or 1 << 3")] string value,
        [Description("Optional: true allows the value change for a [Flags] or persisted enum. Default false.")] bool allowValueChange = false,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("editEnumMember", new { file, line, value, allowValueChange, confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return reply;
    }

    [McpServerTool]
    [Description("Change a method's parameter list with Roslyn, and rewrite every call site to match, with checks. parameters is the new list in order, each {declaration, fromOld, valueForCallers}: declaration is the parameter as written in the new signature (such as \"int b\" or \"CancellationToken ct = default\"); fromOld is the index of the old parameter it keeps (omit it for a new parameter); valueForCallers is the argument every existing call gets for a new parameter (an expression, or default). A new parameter without a default needs valueForCallers. A removed parameter's argument is dropped only when it has no side effects. Calls it cannot rewrite (method groups, delegates, constructors, attributes, params) refuse the change and are listed as uncovered. A method that overrides, implements or is overridden is refused. The doc comment's param tags follow, as in ide_edit_method, and docs needs an entry for each new parameter when the method has a doc comment. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied it runs the tests that call the method with coverage; pass runTests false to skip. Visual Studio only.")]
    public async Task<string> IdeChangeSignature(
        [Description("Full path of the source file")] string file,
        [Description("1-based line inside the method (its name line is best)")] int line,
        [Description("The new parameter list in order: array of {declaration, fromOld, valueForCallers}.")] object[] parameters,
        [Description("Optional: 1-based column. Default 1.")] int? column = null,
        [Description("Optional: array of {tag, name, text} for doc tags: tag is param, typeparam or returns. Needed for each new parameter when the method has a doc comment; the tool never invents text.")] object[]? docs = null,
        [Description("Optional: new return type, such as \"long\" or \"Task<int>\". Callers that no longer compile are refused.")] string? returnType = null,
        [Description("Optional: new accessibility: public, internal, protected, private, protected internal or private protected. Other modifiers are kept.")] string? accessibility = null,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Run the tests that call the method, with coverage, after the change is applied. Default true.")] bool runTests = true,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("changeSignature", new { file, line, column = column ?? 1, parameters, docs = docs ?? [], returnType, accessibility, confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return await MethodTestRunner.AttachAsync(reply, confirm && runTests, ct).ConfigureAwait(false);
    }

    [McpServerTool]
    [Description("Add one parameter to a method and thread it up its callers, using Roslyn, with checks. declaration is the parameter as written (for example \"CancellationToken ct = default\"). Each call passes the caller's own parameter of the same type when it has one; a caller within depth levels gets the parameter too and passes it on; any call above that passes valueAtTop (a literal or expression such as CancellationToken.None). depth defaults to 1 (the direct callers). Every method it changes is listed in the reply. Overrides, implementations, params methods and calls that cannot be rewritten refuse the change. Doc comments are not updated (the reply warns). Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied it runs the tests that call the method with coverage; pass runTests false to skip. Visual Studio only.")]
    public async Task<string> IdeThreadParameter(
        [Description("Full path of the source file")] string file,
        [Description("1-based line inside the method to add the parameter to (its name line is best)")] int line,
        [Description("The parameter as written, such as \"CancellationToken ct = default\"")] string declaration,
        [Description("The argument a call passes at the top of the threading, such as CancellationToken.None")] string valueAtTop,
        [Description("Optional: 1-based column. Default 1.")] int? column = null,
        [Description("Optional: how many caller levels above the method get the parameter. Default 1.")] int? depth = null,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Run the tests that call the method, with coverage, after the change is applied. Default true.")] bool runTests = true,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("threadParameter", new { file, line, column = column ?? 1, declaration, valueAtTop, depth = depth ?? 1, confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return await MethodTestRunner.AttachAsync(reply, confirm && runTests, ct).ConfigureAwait(false);
    }

    [McpServerTool]
    [Description("Add one C# method to the type that contains a line, using Roslyn, with checks. The new method goes at the end of the type, or after the member at afterLine. If neighbouring methods have /// doc comments, the new one gets a doc comment in the same layout from newSummary and docs (a {tag, name, text} entry for each parameter, type parameter and a non-void return); the tool never invents text and warns when it is missing. The file is formatted, a new compile error refuses the edit, and new warnings and notices are returned. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied it runs the tests that call the method (or the affected test projects when none do) with coverage and returns each test's status and the method's line coverage in test_run; pass runTests false to skip. Visual Studio only.")]
    public async Task<string> IdeAddMethod(
        [Description("Full path of the source file")] string file,
        [Description("1-based line inside the target type")] int line,
        [Description("Full text of the new method declaration, without a doc comment")] string newMethodText,
        [Description("Optional: insert after the member that contains this 1-based line. Default is the end of the type.")] int? afterLine = null,
        [Description("Optional: summary text for the new method's doc comment.")] string? newSummary = null,
        [Description("Optional: array of {tag, name, text} for param, typeparam and returns tags.")] object[]? docs = null,
        [Description("Optional: fields and properties the method needs, added to its type in the same checked change. Array of {name, type, accessibility (required: public, internal, protected, private, protected internal, private protected), backing (auto, field, readonly-field, field-only, expression), setter, required, initializer, attributes, summary}.")] object[]? members = null,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Run the tests that call the method, with coverage, after the change is applied. Default true.")] bool runTests = true,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("addMethod", new { file, line, newMethodText, afterLine, newSummary, docs = docs ?? [], members = members ?? [], confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return await MethodTestRunner.AttachAsync(reply, confirm && runTests, ct).ConfigureAwait(false);
    }

    [McpServerTool]
    [Description("Add an overload of the method at a line, using Roslyn, with checks. The overload has the same name, goes after the method's last overload, and gets a doc comment like the original's (newSummary and docs supply the text; the tool never invents it). It is refused if it changes which method any existing call binds to, if the file then has a new compile error, or if its signature matches an existing method. The file is formatted and new warnings and notices are returned. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied it runs the tests that call the method (or the affected test projects when none do) with coverage and returns each test's status and the method's line coverage in test_run; pass runTests false to skip. Visual Studio only.")]
    public async Task<string> IdeAddOverload(
        [Description("Full path of the source file")] string file,
        [Description("1-based line inside the method to overload")] int line,
        [Description("Full text of the new overload, without a doc comment")] string newMethodText,
        [Description("Optional: summary text for the overload's doc comment. Without it the original's summary is kept.")] string? newSummary = null,
        [Description("Optional: array of {tag, name, text} for param, typeparam and returns tags that differ from the original.")] object[]? docs = null,
        [Description("Optional: fields and properties the method needs, added to its type in the same checked change. Array of {name, type, accessibility (required: public, internal, protected, private, protected internal, private protected), backing (auto, field, readonly-field, field-only, expression), setter, required, initializer, attributes, summary}.")] object[]? members = null,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Run the tests that call the method, with coverage, after the change is applied. Default true.")] bool runTests = true,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("addOverload", new { file, line, newMethodText, newSummary, docs = docs ?? [], members = members ?? [], confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return await MethodTestRunner.AttachAsync(reply, confirm && runTests, ct).ConfigureAwait(false);
    }

    [McpServerTool]
    [Description("TDD step 1: add a test and the method it tests, then run the test to show it red. The method goes into the type at methodFile:methodLine; give it without a body (ending in ;) and it is added as a stub that throws NotImplementedException, so the test compiles and fails. The test goes into the class at testFile:testLine (testMethodText includes its attribute). The two must be in different files. Both are formatted and checked like ide_add_method. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied the new test runs with coverage and tdd.status says red (failed, as wanted), green (it passed before the method existed, so it may not test anything) or not_run. Visual Studio only.")]
    public async Task<string> IdeTddAddTest(
        [Description("Full path of the test file")] string testFile,
        [Description("1-based line inside the test class")] int testLine,
        [Description("Full text of the test method, with its [Fact], [Test] or [TestMethod] attribute")] string testMethodText,
        [Description("Full path of the file that gets the new method")] string methodFile,
        [Description("1-based line inside the type that gets the new method")] int methodLine,
        [Description("Full text of the method under test. Without a body it becomes a stub that throws.")] string methodText,
        [Description("Optional: summary for the method's doc comment, when its neighbours have one.")] string? newSummary = null,
        [Description("Optional: array of {tag, name, text} for the method's param, typeparam and returns doc tags.")] object[]? docs = null,
        [Description("Optional: fields and properties the method needs, added to its type in the same checked change. Array of {name, type, accessibility (required: public, internal, protected, private, protected internal, private protected), backing (auto, field, readonly-field, field-only, expression), setter, required, initializer, attributes, summary}.")] object[]? members = null,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Run the new test with coverage after the change is applied. Default true.")] bool runTests = true,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("tddAddTest", new { testFile, testLine, testMethodText, methodFile, methodLine, methodText, newSummary, docs = docs ?? [], members = members ?? [], confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return await MethodTestRunner.AttachAsync(reply, confirm && runTests, ct, MethodTestRunner.TddRed).ConfigureAwait(false);
    }

    [McpServerTool]
    [Description("TDD step 2: replace the body of a test, then run it and return its result. newBody is a block or the statements of the new body; the test's attribute and signature stay. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied the test runs, and tdd.status says red or green. Give targetFile and targetLine (a line inside the method under test) to also get that method's line coverage. Then implement the method with ide_edit_method, which runs the tests that call it. Visual Studio only.")]
    public async Task<string> IdeTddUpdateTest(
        [Description("Full path of the test file")] string file,
        [Description("1-based line inside the test method")] int line,
        [Description("New body: a { block } or its statements")] string newBody,
        [Description("Optional: full path of the file with the method under test, for coverage.")] string? targetFile = null,
        [Description("Optional: 1-based line inside the method under test, for coverage.")] int? targetLine = null,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Run the test with coverage after the change is applied. Default true.")] bool runTests = true,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("tddUpdateTest", new { file, line, newBody, targetFile, targetLine, confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return await MethodTestRunner.AttachAsync(reply, confirm && runTests, ct, MethodTestRunner.TddUpdate).ConfigureAwait(false);
    }

    [McpServerTool]
    [Description("List C# members and types with no references, for review before deleting them: private and internal ones by default, public ones when includePublic is true (other assemblies may use public ones). Read only. Each entry gives its kind, name, file and line, and a confidence: members with attributes, interfaces or constructors may be used by reflection, serialization or dependency injection, and are marked low. Feeds ide_delete_member and ide_delete_type. Visual Studio only.")]
    public async Task<string> IdeListUnused(
        [Description("Optional: one project's name. Default: every project in the solution.")] string? project = null,
        [Description("Include public members and types. Default false.")] bool includePublic = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        await IdeInstanceRouter.CallAsync("listUnused", new { project, includePublic }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);

    [McpServerTool]
    [Description("Delete one C# member (method, property, field, event, constructor, operator, indexer, enum member or interface member) using Roslyn, with checks. Refused while any reference remains: give a callerEdits entry for each (file, line, column from ide_semantic_references; newText replaces the call, use an empty value only where that leaves valid code). Refused when the member overrides or implements another member, when an interface member has implementers, and when tests call it unless their names are given in deleteTests (those tests are not deleted). Removes the member and its doc comment. A new compile error refuses the change. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Visual Studio only.")]
    public async Task<string> IdeDeleteMember(
        [Description("Full path of the source file")] string file,
        [Description("1-based line inside the member to delete")] int line,
        [Description("Optional: 1-based column. Default 1.")] int? column = null,
        [Description("Optional: array of {file, line, column, newText} for each reference that is removed or rewritten.")] object[]? callerEdits = null,
        [Description("Optional: names of tests that call the member and may stay while it is removed (they are not deleted).")] string[]? deleteTests = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Run the affected tests after the change is applied. Default true.")] bool runTests = true,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("deleteMember", new { file, line, column, callerEdits = callerEdits ?? [], deleteTests = deleteTests ?? [], confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return await MethodTestRunner.AttachAsync(reply, confirm && runTests, ct).ConfigureAwait(false);
    }

    [McpServerTool]
    [Description("Delete one C# type (class, struct, interface, record or enum, top-level or nested) using Roslyn, with checks. Refused while any reference outside the type remains: give a callerEdits entry for each (file, line, column from ide_semantic_references; newText replaces the reference). Refused while tests call it unless their names are in deleteTests (those tests are not deleted). With deleteFile true, the file is deleted too when the type was its only declaration (SDK-style projects only). A new compile error refuses the change. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Visual Studio only.")]
    public async Task<string> IdeDeleteType(
        [Description("Full path of the file that declares the type")] string file,
        [Description("1-based line inside the type declaration")] int line,
        [Description("Optional: 1-based column. Default 1.")] int? column = null,
        [Description("Optional: array of {file, line, column, newText} for each reference outside the type.")] object[]? callerEdits = null,
        [Description("Optional: names of tests that call the type and may stay while it is removed.")] string[]? deleteTests = null,
        [Description("Delete the file too when the type was its only declaration. Default false.")] bool deleteFile = false,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Run the affected tests after the change is applied. Default true.")] bool runTests = true,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("deleteType", new { file, line, column, callerEdits = callerEdits ?? [], deleteTests = deleteTests ?? [], deleteFile, confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return await MethodTestRunner.AttachAsync(reply, confirm && runTests, ct).ConfigureAwait(false);
    }

    [McpServerTool]
    [Description("Rename a namespace across the solution, using Roslyn, with checks. The namespace and every namespace under it change: each declaration, using, qualified name, alias and cref that spells it out. It is refused if the name is not a valid dotted name, if no source file declares the namespace, if it is also declared in a referenced assembly, if a type in it would collide with a type already in the new namespace, if a declaration is a block nested inside another namespace block, or if code names it relative to an enclosing namespace (those references need callerEdits entries with file, line, column and newText). Strings that still mention the old name are listed in string_mentions and not changed. A new compile error refuses the change; new warnings and notices are returned. When the project's folders follow its namespaces, files that no longer match are warned about, or moved when moveFiles is true (SDK-style projects only; ask the user first). Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied it runs the affected test projects. Visual Studio only.")]
    public async Task<string> IdeRenameNamespace(
        [Description("The namespace to rename, such as Company.Product.Old")] string oldNamespace,
        [Description("The new name, such as Company.Product.New")] string newNamespace,
        [Description("Move files so their folder matches the new namespace (SDK-style projects whose folders already follow namespaces). Default false: only warn.")] bool moveFiles = false,
        [Description("Optional: array of {file, line, column, newText} for references written relative to an enclosing namespace.")] object[]? callerEdits = null,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Run the affected test projects after the change is applied. Default true.")] bool runTests = true,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("renameNamespace", new { oldNamespace, newNamespace, moveFiles, callerEdits = callerEdits ?? [], confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return await MethodTestRunner.AttachAsync(reply, confirm && runTests, ct).ConfigureAwait(false);
    }

    [McpServerTool]
    [Description("Move one top-level type to another namespace, using Roslyn, with checks. The type's declaration moves, every qualified use is rewritten, and code that used the type by its simple name gets a using for the new namespace; the moved type gets the usings it used to have implicitly, and usings of the old namespace that nothing needs any more are removed. It is refused if the new name is not valid, the type is nested or in the global namespace, its file also declares other types (or a partial part's file does), a type of that name already exists in the new namespace, or code names the type relative to an enclosing namespace (those need callerEdits entries). A new compile error refuses the change. The same moveFiles rule as ide_rename_namespace applies. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied it runs the affected test projects. Visual Studio only.")]
    public async Task<string> IdeMoveType(
        [Description("Full path of the file that declares the type")] string file,
        [Description("1-based line inside the type declaration")] int line,
        [Description("The namespace to move it to")] string newNamespace,
        [Description("Move the file so its folder matches the new namespace (SDK-style projects whose folders already follow namespaces). Default false: only warn.")] bool moveFiles = false,
        [Description("Optional: array of {file, line, column, newText} for references written relative to an enclosing namespace.")] object[]? callerEdits = null,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Run the affected test projects after the change is applied. Default true.")] bool runTests = true,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync("moveType", new { file, line, newNamespace, moveFiles, callerEdits = callerEdits ?? [], confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return await MethodTestRunner.AttachAsync(reply, confirm && runTests, ct).ConfigureAwait(false);
    }

    [McpServerTool]
    [Description("Add one or more fields or properties to the type that contains a line, using Roslyn, with checks. For an indexer, an event, an event field or a delegate, give its full C# text as declaration (only that one member; name and type are optional and unused). For each field or property you give its name, type, accessibility (always required: public, internal, protected, private, protected internal or private protected) and how it is backed: auto ({ get; set; }), field (a private backing field and a property over it), readonly-field, field-only (a plain field) or expression (a get-only property computed from initializer). Also setter (set, init, none, or \"private set\" style), required, initializer, attributes (for example \"Required\" or \"JsonProperty(\\\"x\\\")\") and summary. They go after the type's last field or property, in its layout, with doc comments in the neighbours' style when they have them. The file is formatted, a new compile error refuses the change, and new warnings and notices are returned. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied it runs the tests that call the type's members, if any. Visual Studio only.")]
    public async Task<string> IdeAddMember(
        [Description("Full path of the source file")] string file,
        [Description("1-based line inside the target type")] int line,
        [Description("Array of {name, type, accessibility, backing, setter, required, initializer, attributes, summary}")] object[] members,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        await IdeInstanceRouter.CallAsync("addMember", new { file, line, members, confirm, planHash }, solutionPath, ct, ide, instancePid).ConfigureAwait(false);

    [McpServerTool]
    [Description("Change an existing field or property, whatever its accessibility, using Roslyn, with checks. Set only what changes: newType, accessibility, setter (set, init, none, \"private set\" style), required, backing (auto or field: swap between an auto property and a property over a private field), addAttributes, removeAttributes, newSummary, or newName. Every reference is checked: a change to the type, accessibility, setter or required needs a callerEdits entry (file, line, column from ide_semantic_references, and newText for the invocation or name) for each reference, or it is refused; a rename updates its references itself and must be a call of its own; attribute, backing and summary changes touch no reference. A change on a property that overrides or implements another member is refused. A new compile error refuses the change. The file is formatted and new warnings and notices are returned. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Once applied it runs the tests that call the member, with coverage. Visual Studio only.")]
    public async Task<string> IdeEditMember(
        [Description("Full path of the source file")] string file,
        [Description("1-based line inside the field, property, indexer, event or delegate")] int line,
        [Description("Optional: for an indexer, event, event field or delegate, its full new C# text. Alone in its call, except confirm and callerEdits. Its kind must match the member at the line; a change to its kind, access, type or parameters is refused while other code uses it.")] string? declaration = null,
        [Description("Optional: new name (alone in its call)")] string? newName = null,
        [Description("Optional: new type")] string? newType = null,
        [Description("Optional: new accessibility: public, internal, protected, private, protected internal or private protected")] string? accessibility = null,
        [Description("Optional: set, init, none, or an accessibility and set or init such as \"private set\"")] string? setter = null,
        [Description("Optional: true or false to add or remove the required modifier")] bool? required = null,
        [Description("Optional: auto or field, to swap how the property is backed")] string? backing = null,
        [Description("Optional: attributes to add, such as \"Required\" or \"JsonProperty(\\\"x\\\")\"")] string[]? addAttributes = null,
        [Description("Optional: names of attributes to remove, such as \"Required\"")] string[]? removeAttributes = null,
        [Description("Optional: new summary for the member's existing doc comment")] string? newSummary = null,
        [Description("Optional: array of {file, line, column, newText} for references, when the change affects what other code sees.")] object[]? callerEdits = null,
        [Description("Optional: the planHash the user was shown. Confirm refuses when the change re-plans to something else.")] string? planHash = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Run the tests that call the member, with coverage, after the change is applied. Default true.")] bool runTests = true,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        var reply = await IdeInstanceRouter.CallAsync(
            "editMember",
            new { file, line, declaration, newName, newType, accessibility, setter, required, backing, addAttributes = addAttributes ?? [], removeAttributes = removeAttributes ?? [], newSummary, callerEdits = callerEdits ?? [], confirm, planHash },
            solutionPath, ct, ide, instancePid).ConfigureAwait(false);
        return await MethodTestRunner.AttachAsync(reply, confirm && runTests, ct).ConfigureAwait(false);
    }

    [McpServerTool]
    [Description("Make sure this server holds an events subscription to a Visual Studio instance. Subscriptions are also made automatically for every running instance, so this is only needed if one was closed. Returns the instance pid. Read events with ide_read_events.")]
    public async Task<string> IdeSubscribeEvents(
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default)
    {
        // The health reply carries the process id, which names the events pipe.
        var health = JsonDocument.Parse(await IdeInstanceRouter.CallAsync("health", new { }, solutionPath, ct, ide, instancePid).ConfigureAwait(false));
        if (!health.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            return health.RootElement.GetRawText();
        }

        var pid = health.RootElement.GetProperty("pid").GetInt32();
        var created = await EventSubscriptions.EnsureSubscribedAsync(pid, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { ok = true, pid, created });
    }

    [McpServerTool]
    [Description("Read events from a Visual Studio instance, after a sequence number (0 for everything kept). Events are kept for the last 500 per instance from the time this server started. Events: solutionOpened, solutionClosing, buildBegin, buildDone, enterBreakMode, enterRunMode, contextChanged, documentSaved, documentOpened, documentClosing. Read-only.")]
    public string IdeReadEvents(
        [Description("Optional: process id of the instance. Needed when more than one instance is subscribed.")] int? instancePid = null,
        [Description("Return events with a sequence number greater than this. Default 0.")] long sinceSeq = 0,
        [Description("Optional: only these event names. Omit for all.")] string[]? events = null,
        [Description("Optional: info, warning or error. Events below this are left out. Default info.")] string? minSeverity = null,
        [Description("Optional: most events to return (1 to 200). Default 20. The reply counts the rest in omitted; pass next_seq as sinceSeq to continue.")] int? limit = null,
        [Description("Optional: longest text kept in one event (characters). Longer text is cut and marked truncated. Default 300.")] int? maxTextChars = null)
    {
        if (!EventSeverityMap.TryParse(minSeverity, out var severity))
        {
            throw new ArgumentException(Strings.Format("UnknownMinSeverity", minSeverity));
        }

        var pid = instancePid ?? EventSubscriptions.OnlySubscribedPid();
        // Default scope (plan 4.3): run results, status and errors. Pass events to ask for others by name.
        var names = events is { Length: > 0 } ? events : DefaultEventNames;
        var options = new EventFilterOptions(
            Names: null,
            MinSeverity: severity,
            Limit: limit ?? 20,
            MaxTextChars: maxTextChars ?? 300);
        return JsonSerializer.Serialize(EventSubscriptions.Read(pid, sinceSeq, names, options));
    }

    [McpServerTool]
    [Description("Stop this server's events subscription to one instance. Automatic subscription resumes on the next check unless the instance is closed.")]
    public string IdeUnsubscribeEvents(
        [Description("Process id of the instance")] int instancePid)
    {
        EventSubscriptions.Unsubscribe(instancePid);
        return JsonSerializer.Serialize(new { ok = true, pid = instancePid });
    }

    [McpServerTool]
    [Description("Change the break-when-thrown and/or break-when-user-unhandled setting of one debugger exception. Without confirm it only returns the change. Show the plan to the user and get their yes before setting confirm to true.")]
    public Task<string> IdeSetExceptionBreak(
        [Description("Exception group name, as listed by ide_list_exceptions")] string group,
        [Description("Exception name, as listed by ide_list_exceptions")] string name,
        [Description("Optional: true to break when thrown, false to not break")] bool? breakWhenThrown = null,
        [Description("Optional: true to break when user-unhandled, false to not break")] bool? breakWhenUserUnhandled = null,
        [Description("Set true to apply the change. Default false returns the plan only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("setExceptionBreak", new { group, name, breakWhenThrown, breakWhenUserUnhandled, confirm }, solutionPath, ct, ide, instancePid);
}
