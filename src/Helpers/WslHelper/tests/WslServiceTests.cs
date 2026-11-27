using System.Text.Json;
using System.Text.Json.Nodes;

using AiDevHelpers.Common.Processes;
using AiDevHelpers.Helpers.WslHelper;

using Xunit;

namespace AiDevHelpers.Helpers.WslHelper.Tests;

/// <summary>Records every request and answers with a scripted <see cref="CommandResult" />. Never starts a process.</summary>
internal sealed class FakeWslCommands(Func<CommandRequest, CommandResult>? respond = null) : IWslCommands
{
    private readonly Func<CommandRequest, CommandResult> _respond = respond ?? (_ => new CommandResult { ExitCode = 0, StdOut = "ok" });

    public List<CommandRequest> Calls { get; } = [];

    public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
    {
        Calls.Add(request);
        return Task.FromResult(_respond(request));
    }
}

public sealed class WslServiceTests
{
    private const string ListOutput =
        "  NAME            STATE           VERSION\r\n" +
        "* Ubuntu          Running         2\r\n" +
        "  Debian          Stopped         1\r\n";

    private const string ProbeReady = "READY\n";

    private static readonly string[] ProbeArgsPrefix = ["-d", "Ubuntu", "--", "/bin/bash", "-c"];

    private static bool IsList(CommandRequest r) =>
        r.Arguments.Count == 2 && r.Arguments[0] == "-l" && r.Arguments[1] == "-v";

    private static CommandResult Ok(string stdout = "") => new() { ExitCode = 0, StdOut = stdout };

    private static FakeWslCommands ListingFake(Func<CommandRequest, CommandResult>? other = null) =>
        new(r => IsList(r) ? Ok(ListOutput) : (other?.Invoke(r) ?? Ok()));

    // Simulates text as it would look if the bytes were UTF-16 LE and read without decoding: a NUL after each char.
    private static string Utf16Style(string text) => string.Concat(text.Select(c => c + "\0"));

    private static string[] Strings(JsonNode? node) =>
        node!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    // ── Distro list parsing (through status) ────────────────────────────────────

    [Fact]
    public async Task Status_ParsesRowsDefaultStateVersionAndSkipsHeader()
    {
        FakeWslCommands fake = ListingFake();

        WslStatusResult s = await new WslService(fake).StatusAsync();

        Assert.True(s.Ok);
        Assert.True(s.Available);
        Assert.Equal(2, s.DistroCount);
        Assert.Equal("Ubuntu", s.DefaultDistro);
        Assert.Equal("Ubuntu", s.Distros[0].Name);
        Assert.True(s.Distros[0].IsDefault);
        Assert.Equal("Running", s.Distros[0].State);
        Assert.Equal(2, s.Distros[0].Version);
        Assert.Equal("Debian", s.Distros[1].Name);
        Assert.False(s.Distros[1].IsDefault);
        Assert.Equal("Stopped", s.Distros[1].State);
        Assert.Equal(1, s.Distros[1].Version);
    }

    [Fact]
    public async Task Status_ParsesUtf16StyleTextWithNulsAndBom()
    {
        // BOM first, then the UTF-16 LE text with a NUL after every character (what UTF-8 decoding leaves behind).
        string text = "﻿" + Utf16Style("  NAME STATE VERSION\r\n* Ubuntu Running 2\r\n");
        FakeWslCommands fake = new(r => IsList(r) ? Ok(text) : Ok());

        WslStatusResult s = await new WslService(fake).StatusAsync();

        WslDistro distro = Assert.Single(s.Distros);
        Assert.Equal("Ubuntu", distro.Name);
        Assert.True(distro.IsDefault);
        Assert.Equal("Running", distro.State);
        Assert.Equal(2, distro.Version);
        Assert.Equal("Ubuntu", s.DefaultDistro);
    }

    [Fact]
    public async Task Status_UsesWslUtf8EnvironmentAndListArguments()
    {
        FakeWslCommands fake = ListingFake();

        await new WslService(fake).StatusAsync();

        CommandRequest call = Assert.Single(fake.Calls);
        Assert.Equal("wsl.exe", call.FileName);
        Assert.Equal(new[] { "-l", "-v" }, call.Arguments.ToArray());
        Assert.NotNull(call.Environment);
        Assert.Equal("1", call.Environment["WSL_UTF8"]);
    }

    [Fact]
    public async Task Status_WhenWslMissing_ReportsUnavailableWithoutThrowing()
    {
        FakeWslCommands fake = new(_ => new CommandResult { Launched = false, LaunchError = "wsl.exe not found" });

        WslStatusResult s = await new WslService(fake).StatusAsync();

        Assert.True(s.Ok);
        Assert.False(s.Available);
        Assert.Equal(0, s.DistroCount);
        Assert.Empty(s.Distros);
        Assert.Null(s.DefaultDistro);
    }

    [Fact]
    public async Task Status_ListFailure_ReturnsStderrAsError()
    {
        FakeWslCommands fake = new(_ => new CommandResult { ExitCode = 1, StdErr = "boom" });

        WslStatusResult s = await new WslService(fake).StatusAsync();

        Assert.False(s.Ok);
        Assert.Contains("boom", s.Error);
    }

    // ── Distro name validation ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Ubuntu")]
    [InlineData("Ubuntu-24.04")]
    [InlineData("debian_12")]
    [InlineData("a")]
    public void ValidDistroName_Accepted(string name) =>
        Assert.True(WslService.IsValidDistroName(name));

    [Theory]
    [InlineData("-rf")]
    [InlineData("--import")]
    [InlineData("Ubuntu; rm -rf /")]
    [InlineData("Ubuntu name")]
    [InlineData("$(whoami)")]
    [InlineData("Ubuntu\n")]
    [InlineData(".hidden")]
    [InlineData("")]
    public void InvalidDistroName_Rejected(string name) =>
        Assert.False(WslService.IsValidDistroName(name));

    [Fact]
    public void NullDistroName_Rejected() =>
        Assert.False(WslService.IsValidDistroName(null));

    [Fact]
    public async Task Run_InvalidDistroName_RefusedBeforeAnyCommand()
    {
        FakeWslCommands fake = ListingFake();

        WslRunResult r = await new WslService(fake).RunAsync("echo hi", "-rf");

        Assert.False(r.Ok);
        Assert.Contains("Invalid distro name", r.Error);
        Assert.Empty(fake.Calls);
    }

    // ── Run ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_BuildsExactArgumentList()
    {
        const string command = "echo 'a b' | cat > /tmp/x";
        FakeWslCommands fake = new(_ => Ok("hi\n"));

        WslRunResult r = await new WslService(fake).RunAsync(command, "Ubuntu");

        CommandRequest call = Assert.Single(fake.Calls);
        Assert.Equal("wsl.exe", call.FileName);
        Assert.Equal(new[] { "-d", "Ubuntu", "--", "/bin/bash", "-c", command }, call.Arguments.ToArray());
        Assert.Equal(TimeSpan.FromSeconds(120), call.Timeout);
        Assert.Equal(20_000, call.MaxOutputChars);
        Assert.True(r.Ok);
        Assert.Equal("Ubuntu", r.Distro);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("hi", r.Stdout);
        Assert.False(r.TimedOut);
        Assert.False(r.Capped);
        Assert.Null(r.ExecutionError);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    [InlineData(-5)]
    public async Task Run_TimeoutOutOfRange_Refused(int seconds)
    {
        FakeWslCommands fake = new(_ => Ok());

        WslRunResult r = await new WslService(fake).RunAsync("true", "Ubuntu", seconds);

        Assert.False(r.Ok);
        Assert.Contains("timeoutSeconds must be between 1 and 600.", r.Error);
        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(600)]
    public async Task Run_TimeoutBoundaries_Accepted(int seconds)
    {
        FakeWslCommands fake = new(_ => Ok());

        await new WslService(fake).RunAsync("true", "Ubuntu", seconds);

        Assert.Equal(TimeSpan.FromSeconds(seconds), Assert.Single(fake.Calls).Timeout);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Run_EmptyCommand_Refused(string command)
    {
        FakeWslCommands fake = new(_ => Ok());

        WslRunResult r = await new WslService(fake).RunAsync(command, "Ubuntu");

        Assert.False(r.Ok);
        Assert.Contains("command is required", r.Error);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Run_Timeout_ReturnsTimedOutWithExitMinusOne()
    {
        FakeWslCommands fake = new(_ => new CommandResult { TimedOut = true, ExitCode = -1 });

        WslRunResult r = await new WslService(fake).RunAsync("sleep 999", "Ubuntu", 1);

        Assert.True(r.TimedOut);
        Assert.Equal(-1, r.ExitCode);
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Run_LaunchError_SetsExecutionErrorAndExitMinusOne()
    {
        FakeWslCommands fake = new(_ => new CommandResult { Launched = false, LaunchError = "wsl.exe not found" });

        WslRunResult r = await new WslService(fake).RunAsync("true", "Ubuntu");

        Assert.False(r.Ok);
        Assert.Equal(-1, r.ExitCode);
        Assert.Contains("wsl.exe not found", r.ExecutionError);
    }

    [Fact]
    public async Task Run_OutputCapAndTrailingNewlineTrim()
    {
        string longOut = new string('a', 25_000) + "\r\n";
        FakeWslCommands fake = new(_ => new CommandResult { ExitCode = 0, StdOut = longOut, StdErr = "err\r\n\r\n" });

        WslRunResult r = await new WslService(fake).RunAsync("cat big", "Ubuntu");

        Assert.Equal(20_000, r.Stdout.Length);
        Assert.True(r.Capped);
        Assert.Equal("err", r.Stderr);
    }

    [Fact]
    public async Task Run_CappedFlagFromRunnerIsReported()
    {
        FakeWslCommands fake = new(_ => new CommandResult { ExitCode = 0, StdOut = "short", StdErrTruncated = true });

        WslRunResult r = await new WslService(fake).RunAsync("x", "Ubuntu");

        Assert.Equal("short", r.Stdout);
        Assert.True(r.Capped);
    }

    [Fact]
    public async Task Run_NonZeroExit_IsDataNotError()
    {
        FakeWslCommands fake = new(_ => new CommandResult { ExitCode = 3, StdErr = "nope" });

        WslRunResult r = await new WslService(fake).RunAsync("false", "Ubuntu");

        Assert.True(r.Ok);
        Assert.Equal(3, r.ExitCode);
        Assert.Equal("nope", r.Stderr);
    }

    // ── Health ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Health_Healthy_ParsesKeyValues()
    {
        const string probe =
            "KERNEL=Linux 5.15.167.4-microsoft-standard-WSL2\n" +
            "USER=richard\n" +
            "OS=Ubuntu 24.04 LTS\n" +
            "APT=1\n" +
            "SUDO=1\n";
        FakeWslCommands fake = new(_ => Ok(probe));

        WslHealthResult h = await new WslService(fake).HealthAsync("Ubuntu");

        Assert.True(h.Ok);
        Assert.Equal("Ubuntu", h.Distro);
        Assert.True(h.Healthy);
        Assert.Equal("Linux 5.15.167.4-microsoft-standard-WSL2", h.Kernel);
        Assert.Equal("richard", h.User);
        Assert.Equal("Ubuntu 24.04 LTS", h.OsDescription);
        Assert.True(h.HasApt);
        Assert.True(h.HasSudo);
        Assert.Null(h.Detail);
    }

    [Fact]
    public async Task Health_MissingKernel_IsUnhealthy()
    {
        FakeWslCommands fake = new(_ => Ok("USER=root\nOS=Debian\nAPT=0\nSUDO=0\n"));

        WslHealthResult h = await new WslService(fake).HealthAsync("Ubuntu");

        Assert.True(h.Ok);
        Assert.False(h.Healthy);
        Assert.False(h.HasApt);
        Assert.False(h.HasSudo);
        Assert.NotNull(h.Detail);
    }

    [Fact]
    public async Task Health_FailedProbe_ReturnsStderrAsDetail()
    {
        FakeWslCommands fake = new(_ => new CommandResult { ExitCode = 1, StdErr = "boom\n" });

        WslHealthResult h = await new WslService(fake).HealthAsync("Ubuntu");

        Assert.True(h.Ok);
        Assert.False(h.Healthy);
        Assert.Equal("boom", h.Detail);
    }

    [Fact]
    public async Task Health_FailedProbeWithoutStderr_ReportsExitCode()
    {
        FakeWslCommands fake = new(_ => new CommandResult { ExitCode = 7 });

        WslHealthResult h = await new WslService(fake).HealthAsync("Ubuntu");

        Assert.False(h.Healthy);
        Assert.Contains("7", h.Detail);
    }

    [Fact]
    public async Task Health_RunsSingleBashProbeWith120SecondTimeout()
    {
        FakeWslCommands fake = new(_ => Ok("KERNEL=x\n"));

        await new WslService(fake).HealthAsync("Ubuntu");

        CommandRequest call = Assert.Single(fake.Calls);
        Assert.Equal(TimeSpan.FromSeconds(120), call.Timeout);
        Assert.Equal(ProbeArgsPrefix, call.Arguments.Take(5).ToArray());
        Assert.Equal(6, call.Arguments.Count);
    }

    [Fact]
    public async Task Health_OmittedDistro_UsesDefaultFromListing()
    {
        FakeWslCommands fake = ListingFake(_ => Ok("KERNEL=x\n"));

        WslHealthResult h = await new WslService(fake).HealthAsync();

        Assert.Equal("Ubuntu", h.Distro);
        Assert.True(h.Healthy);
        Assert.Equal(2, fake.Calls.Count);
        Assert.True(IsList(fake.Calls[0]));
    }

    // ── debug_setup ─────────────────────────────────────────────────────────────

    private static FakeWslCommands ProbeFake(string probeStdout = ProbeReady) =>
        new(_ => Ok(probeStdout));

    [Fact]
    public async Task DebugSetup_UnknownRuntime_RefusedWithoutCommands()
    {
        FakeWslCommands fake = ProbeFake();

        WslDebugSetupResult r = await new WslService(fake).DebugSetupAsync("ruby", "/app/main.rb", "Ubuntu");

        Assert.False(r.Ok);
        Assert.Contains("Unknown runtime", r.Error);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task DebugSetup_MissingProgramPath_Refused()
    {
        FakeWslCommands fake = ProbeFake();

        WslDebugSetupResult r = await new WslService(fake).DebugSetupAsync("node", " ", "Ubuntu");

        Assert.False(r.Ok);
        Assert.Contains("programPath", r.Error);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task DebugSetup_Dotnet_LaunchJsonHasContractFields()
    {
        FakeWslCommands fake = ProbeFake();

        WslDebugSetupResult r = await new WslService(fake).DebugSetupAsync("dotnet", "/home/u/app/bin/app.dll", "Ubuntu");

        Assert.True(r.Ok);
        Assert.True(r.BackendReady);
        Assert.False(r.ConfigWritten);
        Assert.Equal("dotnet", r.Runtime);
        JsonObject c = r.LaunchConfig!;
        Assert.Equal("WSL: .NET Launch", c["name"]!.GetValue<string>());
        Assert.Equal("coreclr", c["type"]!.GetValue<string>());
        Assert.Equal("launch", c["request"]!.GetValue<string>());
        Assert.Equal("/home/u/app/bin/app.dll", c["program"]!.GetValue<string>());
        Assert.Equal("${workspaceFolder}", c["cwd"]!.GetValue<string>());
        Assert.False(c["stopAtEntry"]!.GetValue<bool>());
        Assert.Equal("internalConsole", c["console"]!.GetValue<string>());
        JsonObject pipe = c["pipeTransport"]!.AsObject();
        Assert.Equal("${workspaceFolder}", pipe["pipeCwd"]!.GetValue<string>());
        Assert.Equal("wsl.exe", pipe["pipeProgram"]!.GetValue<string>());
        Assert.Equal(new[] { "-d", "Ubuntu", "--" }, Strings(pipe["pipeArgs"]));
        Assert.Equal("~/.vsdbg/vsdbg", pipe["debuggerPath"]!.GetValue<string>());
    }

    [Fact]
    public async Task DebugSetup_Python_LaunchJsonHasContractFields()
    {
        FakeWslCommands fake = ProbeFake();

        WslDebugSetupResult r = await new WslService(fake).DebugSetupAsync("python", "/home/u/app/main.py", "Ubuntu");

        JsonObject c = r.LaunchConfig!;
        Assert.True(r.Ok);
        Assert.Equal("WSL: Python Launch", c["name"]!.GetValue<string>());
        Assert.Equal("debugpy", c["type"]!.GetValue<string>());
        Assert.Equal("launch", c["request"]!.GetValue<string>());
        Assert.Equal("/home/u/app/main.py", c["program"]!.GetValue<string>());
        Assert.Equal("integratedTerminal", c["console"]!.GetValue<string>());
        Assert.Equal("wsl.exe", c["python"]!.GetValue<string>());
        Assert.Equal(new[] { "-d", "Ubuntu", "--", "python3" }, Strings(c["pythonArgs"]));
    }

    [Fact]
    public async Task DebugSetup_Node_LaunchJsonHasContractFields()
    {
        FakeWslCommands fake = ProbeFake();

        WslDebugSetupResult r = await new WslService(fake).DebugSetupAsync("node", "/home/u/app/index.js", "Ubuntu");

        JsonObject c = r.LaunchConfig!;
        Assert.True(r.Ok);
        Assert.Equal("WSL: Node Launch", c["name"]!.GetValue<string>());
        Assert.Equal("node", c["type"]!.GetValue<string>());
        Assert.Equal("launch", c["request"]!.GetValue<string>());
        Assert.Equal("/home/u/app/index.js", c["program"]!.GetValue<string>());
        Assert.Equal("${workspaceFolder}", c["cwd"]!.GetValue<string>());
        Assert.True(c["useWSL"]!.GetValue<bool>());
    }

    [Fact]
    public async Task DebugSetup_ProbeRunsWith30SecondTimeoutAndBackendCheck()
    {
        FakeWslCommands fake = ProbeFake();

        await new WslService(fake).DebugSetupAsync("dotnet", "/app/a.dll", "Ubuntu");

        CommandRequest call = Assert.Single(fake.Calls);
        Assert.Equal(TimeSpan.FromSeconds(30), call.Timeout);
        Assert.Equal(ProbeArgsPrefix, call.Arguments.Take(5).ToArray());
        Assert.Contains(".vsdbg/vsdbg", call.Arguments[5]);
    }

    [Fact]
    public async Task DebugSetup_MissingBackend_ReportsInstallHint()
    {
        FakeWslCommands fake = ProbeFake("MISSING\n");

        WslDebugSetupResult r = await new WslService(fake).DebugSetupAsync("python", "/app/main.py", "Ubuntu");

        Assert.True(r.Ok);
        Assert.False(r.BackendReady);
        Assert.Contains("debugpy", r.InstallHint);
    }

    [Fact]
    public async Task DebugSetup_WriteCreatesLaunchJson()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wslhelper-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            FakeWslCommands fake = ProbeFake();

            WslDebugSetupResult r = await new WslService(fake).DebugSetupAsync(
                "node", "/app/index.js", "Ubuntu", workspaceRoot: dir, write: true);

            Assert.True(r.Ok);
            Assert.True(r.ConfigWritten);
            string path = Path.Combine(dir, ".vscode", "launch.json");
            Assert.True(File.Exists(path));
            JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal("0.2.0", root["version"]!.GetValue<string>());
            JsonObject written = root["configurations"]!.AsArray()[0]!.AsObject();
            Assert.Equal("WSL: Node Launch", written["name"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DebugSetup_ExistingLaunchJson_IsNotOverwritten()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wslhelper-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".vscode"));
        try
        {
            string path = Path.Combine(dir, ".vscode", "launch.json");
            File.WriteAllText(path, "KEEP");
            FakeWslCommands fake = ProbeFake();

            WslDebugSetupResult r = await new WslService(fake).DebugSetupAsync(
                "node", "/app/index.js", "Ubuntu", workspaceRoot: dir, write: true);

            Assert.True(r.Ok);
            Assert.False(r.ConfigWritten);
            Assert.NotNull(r.LaunchConfig);
            Assert.Equal("KEEP", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DebugSetup_WriteWithMissingWorkspaceRoot_Refused()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wslhelper-missing-" + Guid.NewGuid().ToString("N"));
        FakeWslCommands fake = ProbeFake();

        WslDebugSetupResult r = await new WslService(fake).DebugSetupAsync(
            "node", "/app/index.js", "Ubuntu", workspaceRoot: dir, write: true);

        Assert.False(r.Ok);
        Assert.Contains(dir, r.Error);
        Assert.False(Directory.Exists(dir));
        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("python")]
    [InlineData("node")]
    public async Task DebugSetup_ProgramPathNeverOnACommandLine(string runtime)
    {
        const string programPath = "/home/u/my app; rm -rf ~/x && echo '$(id)'.js";
        FakeWslCommands fake = ProbeFake();

        WslDebugSetupResult r = await new WslService(fake).DebugSetupAsync(runtime, programPath, "Ubuntu");

        Assert.True(r.Ok);
        Assert.Equal(programPath, r.LaunchConfig!["program"]!.GetValue<string>());
        foreach (CommandRequest call in fake.Calls)
        {
            Assert.DoesNotContain(programPath, call.FileName);
            Assert.DoesNotContain(call.Arguments, a => a.Contains(programPath, StringComparison.Ordinal));
        }
    }
}
