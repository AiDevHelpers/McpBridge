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

        return RunTestExplorerWithRetryAsync(solutionPath, ide, instancePid, ct);
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
    [Description("Replace one C# method using Roslyn, with checks. The method at the position is replaced by newMethodText (one full method declaration). The edit is refused if it adds compile errors, or if the signature changes and any reference (from ide_semantic_references) has no entry in callerEdits. Each caller edit names a reference by file, line and column and gives the text that replaces the invocation (or the bare name for a method group or nameof). A signature change on a method that overrides, implements or is overridden is refused. If the method has a /// doc comment, its param, typeparam and returns tags are updated to the new signature in the comment's own layout; a new parameter needs its text in docs (the tool never invents text), and newSummary replaces the summary when the purpose changed. Roslyn cannot see reflection, strings, XAML or projects outside the solution, so also search the name with ide_find_in_files. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Visual Studio only.")]
    public Task<string> IdeEditMethod(
        [Description("Full path of the source file")] string file,
        [Description("1-based line inside the method (its name line is best)")] int line,
        [Description("Full replacement text of the method declaration")] string newMethodText,
        [Description("Optional: 1-based column. Default 1.")] int? column = null,
        [Description("Optional: array of {file, line, column, newText} for callers when the signature changes. file, line and column are a reference location from ide_semantic_references.")] object[]? callerEdits = null,
        [Description("Optional: new text for the summary of the method's XML doc comment, when the method's purpose changed.")] string? newSummary = null,
        [Description("Optional: array of {tag, name, text} for doc tags: tag is param, typeparam or returns. Needed for each new parameter or type parameter when the method has a doc comment; the tool never invents text.")] object[]? docs = null,
        [Description("Set true to apply the edit. Default false returns the checks only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("editMethod", new { file, line, column, newMethodText, callerEdits = callerEdits ?? [], newSummary, docs = docs ?? [], confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Add one C# method to the type that contains a line, using Roslyn, with checks. The new method goes at the end of the type, or after the member at afterLine. If neighbouring methods have /// doc comments, the new one gets a doc comment in the same layout from newSummary and docs (a {tag, name, text} entry for each parameter, type parameter and a non-void return); the tool never invents text and warns when it is missing. The file is formatted, a new compile error refuses the edit, and new warnings and notices are returned. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Visual Studio only.")]
    public Task<string> IdeAddMethod(
        [Description("Full path of the source file")] string file,
        [Description("1-based line inside the target type")] int line,
        [Description("Full text of the new method declaration, without a doc comment")] string newMethodText,
        [Description("Optional: insert after the member that contains this 1-based line. Default is the end of the type.")] int? afterLine = null,
        [Description("Optional: summary text for the new method's doc comment.")] string? newSummary = null,
        [Description("Optional: array of {tag, name, text} for param, typeparam and returns tags.")] object[]? docs = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("addMethod", new { file, line, newMethodText, afterLine, newSummary, docs = docs ?? [], confirm }, solutionPath, ct, ide, instancePid);

    [McpServerTool]
    [Description("Add an overload of the method at a line, using Roslyn, with checks. The overload has the same name, goes after the method's last overload, and gets a doc comment like the original's (newSummary and docs supply the text; the tool never invents it). It is refused if it changes which method any existing call binds to, if the file then has a new compile error, or if its signature matches an existing method. The file is formatted and new warnings and notices are returned. Without confirm it only returns the checks. Show them to the user and get their yes before setting confirm to true. Visual Studio only.")]
    public Task<string> IdeAddOverload(
        [Description("Full path of the source file")] string file,
        [Description("1-based line inside the method to overload")] int line,
        [Description("Full text of the new overload, without a doc comment")] string newMethodText,
        [Description("Optional: summary text for the overload's doc comment. Without it the original's summary is kept.")] string? newSummary = null,
        [Description("Optional: array of {tag, name, text} for param, typeparam and returns tags that differ from the original.")] object[]? docs = null,
        [Description("Set true to apply the change. Default false returns the checks only.")] bool confirm = false,
        [Description("Optional: full path of the solution to target")] string? solutionPath = null,
        [Description("Optional: VisualStudio or VsCode. Picks the IDE when several are open.")] string? ide = null,
        [Description("Optional: process id of the instance to target. Use it when several instances have the same solution open.")] int? instancePid = null,
        CancellationToken ct = default) =>
        IdeInstanceRouter.CallAsync("addOverload", new { file, line, newMethodText, newSummary, docs = docs ?? [], confirm }, solutionPath, ct, ide, instancePid);

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
        [Description("Optional: only these event names. Omit for all.")] string[]? events = null)
    {
        var pid = instancePid ?? EventSubscriptions.OnlySubscribedPid();
        return JsonSerializer.Serialize(EventSubscriptions.Read(pid, sinceSeq, events));
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
