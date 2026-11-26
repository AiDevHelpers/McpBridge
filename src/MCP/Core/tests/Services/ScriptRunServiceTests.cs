using System.Text;
using AiDevHelpers.Common.Processes;
using AiDevHelpers.Common.Terminals;
using AiDevHelpers.MCP.Core.Services;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class ScriptRunServiceTests : IDisposable
{
    private const string Pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "script-run-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptRunTracker _tracker = new();
    private readonly List<CommandRequest> _requests = [];
    private readonly List<TerminalChoices> _unused = [];
    private CommandResult _next = new() { ExitCode = 0, StdOut = "done" };
    private TerminalChoices _choices = new([new RegisteredTerminal("pwsh", "PowerShell 7", Pwsh, "7")], []);

    public ScriptRunServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "scripts"));
        File.WriteAllText(Path.Combine(_root, "scripts", "a.ps1"), "Write-Host hi\n");
        File.WriteAllText(Path.Combine(_root, "scripts", "a.rb"), "puts 1\n");
        File.WriteAllText(Path.Combine(_root, "scripts", "a.sh"), "echo hi\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Probe(params string[] files) : ITerminalProbe
    {
        public string? FindOnPath(string fileName) => null;

        public bool FileExists(string path) => files.Contains(path, StringComparer.OrdinalIgnoreCase);

        public string? FileVersion(string path) => "7.5";

        public string? Folder(Environment.SpecialFolder folder) => folder switch
        {
            Environment.SpecialFolder.ProgramFiles => @"C:\Program Files",
            Environment.SpecialFolder.System => @"C:\Windows\System32",
            _ => null,
        };

        public string? Run(string fileName, IReadOnlyList<string> arguments, Encoding encoding) => null;

        public IReadOnlyList<string> WindowsTerminalSettingsFiles() => [];

        public string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private ScriptRunService Service(params string[] installed) => new(
        _tracker,
        new Probe(installed.Length == 0 ? [Pwsh] : installed),
        () => _choices,
        (request, _) =>
        {
            _requests.Add(request);
            return Task.FromResult(_next);
        });

    private Task<ScriptRunReport> Run(ScriptRunService service, string script = @"scripts\a.ps1", params string[] args) =>
        service.RunAsync(_root, script, args, new ScriptWatchOptions(), CancellationToken.None);

    [Fact]
    public async Task AScript_RunsWithTheResolvedInterpreter_ArgumentsAsDataAndTheLimitsFromTheOptions()
    {
        var report = await Run(Service(), @"scripts\a.ps1", "one two", "&calc");

        Assert.True(report.Ok);
        Assert.Equal("done", report.StdOut);
        Assert.Equal("pwsh", report.Interpreter!.Name);
        var request = Assert.Single(_requests);
        Assert.Equal(Pwsh, request.FileName);
        Assert.Equal(["-NoProfile", "-NonInteractive", "-File", Path.Combine(_root, "scripts", "a.ps1"), "one two", "&calc"], request.Arguments);
        Assert.Equal(_root, request.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(300), request.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(60), request.NoOutputTimeout);
    }

    [Fact]
    public async Task ThePerUserLimits_AreApplied()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".aidevhelpers"));
        File.WriteAllText(AiDevHelpers.Common.RepoSettingsFile.UserFilePath(_root), """{ "scriptWatch": { "noOutputSeconds": 5, "timeoutSeconds": 20 } }""");

        await Run(Service());

        Assert.Equal(TimeSpan.FromSeconds(5), _requests[0].NoOutputTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), _requests[0].Timeout);
    }

    [Fact]
    public async Task ARepoThatDoesNotExist_AScriptOutsideIt_AndAMissingFile_AreErrors_WithNothingRun()
    {
        var service = Service();
        var outside = Path.Combine(Path.GetTempPath(), "x.ps1");

        Assert.StartsWith("Error:", (await service.RunAsync(Path.Combine(_root, "nope"), "a.ps1", [], new ScriptWatchOptions(), default)).Error);
        Assert.StartsWith("Error:", (await Run(service, @"..\..\x.ps1")).Error);
        Assert.StartsWith("Error:", (await Run(service, outside)).Error);
        Assert.StartsWith("Error:", (await Run(service, @"scripts\missing.ps1")).Error);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task AnUnknownFileType_IsRefused()
    {
        var report = await Run(Service(), @"scripts\a.rb");

        Assert.False(report.Ok);
        Assert.Contains(".rb", report.Error);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task AMissingInterpreter_IsReportedWithAHint_AndNothingRuns()
    {
        var report = await Run(Service(@"C:\Windows\System32\cmd.exe"), @"scripts\a.sh");

        Assert.False(report.Ok);
        Assert.Contains("winget install Git.Git", report.InstallHint);
        Assert.Equal("bash", report.Interpreter!.Name);
        Assert.Contains("tool_install", report.InstallOffer);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task ADeclinedInstall_IsNotOfferedAgain()
    {
        _choices = new TerminalChoices([], ["install:gitbash"]);

        var report = await Run(Service(@"C:\Windows\System32\cmd.exe"), @"scripts\a.sh");

        Assert.Null(report.InstallOffer);
        Assert.Contains("winget install Git.Git", report.InstallHint);
    }

    [Fact]
    public async Task ThreeFailingRuns_FlagTheScript_AndTheAdviceOffersAVisibleTerminal()
    {
        _next = new CommandResult { ExitCode = 1, StdErr = "boom" };
        var service = Service();

        var first = await Run(service);
        await Run(service);
        var third = await Run(service);

        Assert.Equal(ScriptFlag.None, first.Flag);
        Assert.Equal(ScriptFlag.Repeating, third.Flag);
        Assert.Contains("interactive", third.Advice);
        Assert.Contains("3 times", third.Advice);
    }

    [Fact]
    public async Task ASilentRun_IsHung_AtOnce()
    {
        _next = new CommandResult { ExitCode = -1, Silent = true };

        var report = await Run(Service());

        Assert.False(report.Ok);
        Assert.True(report.Silent);
        Assert.Equal(ScriptFlag.Hung, report.Flag);
        Assert.Contains("nothing for 60 seconds", report.Advice);
    }

    [Fact]
    public async Task ACleanRun_ClearsTheFlag()
    {
        var service = Service();
        _next = new CommandResult { ExitCode = 1 };
        await Run(service);
        await Run(service);
        _next = new CommandResult { ExitCode = 0 };
        await Run(service);
        _next = new CommandResult { ExitCode = 1 };

        Assert.Equal(ScriptFlag.None, (await Run(service)).Flag);
    }

    [Fact]
    public async Task AnUnregisteredTerminalType_GetsANote_ARegisteredOneDoesNot()
    {
        _choices = new TerminalChoices([], []);
        var unregistered = await Run(Service());
        Assert.Contains("elev terminals setup", unregistered.TerminalNote);

        _choices = new TerminalChoices([new RegisteredTerminal("pwsh", "PowerShell 7", Pwsh, "7")], []);
        Assert.Null((await Run(Service())).TerminalNote);
    }

    [Fact]
    public async Task Interactive_RunsInATab_WithoutNonInteractive_AndStillCountsAsARun()
    {
        InteractiveRequest? seen = null;
        var service = new ScriptRunService(
            _tracker,
            new Probe(Pwsh),
            () => _choices,
            (_, _) => throw new InvalidOperationException("the captured runner must not be used"),
            (request, _) =>
            {
                seen = request;
                return Task.FromResult(new InteractiveResult(true, 1, "tab out", "tab err", "s1", "t1", null));
            });

        var first = await service.RunAsync(_root, @"scripts\a.ps1", ["x"], new ScriptWatchOptions(), CancellationToken.None, interactive: true);
        await service.RunAsync(_root, @"scripts\a.ps1", ["x"], new ScriptWatchOptions(), CancellationToken.None, interactive: true);
        var third = await service.RunAsync(_root, @"scripts\a.ps1", ["x"], new ScriptWatchOptions(), CancellationToken.None, interactive: true);

        Assert.True(first.Interactive);
        Assert.Equal(("s1", "t1"), (first.SessionId, first.TabId));
        Assert.Equal("tab out", first.StdOut);
        Assert.False(first.Ok);
        Assert.DoesNotContain("-NonInteractive", seen!.Arguments);
        Assert.Equal(_root, seen.WorkingDirectory);
        Assert.Equal("a.ps1", seen.TabName);
        Assert.Equal(ScriptFlag.Repeating, third.Flag);
    }

    [Fact]
    public async Task Interactive_WithNoTerminalAvailable_IsAnError()
    {
        var report = await Service().RunAsync(_root, @"scripts\a.ps1", [], new ScriptWatchOptions(), CancellationToken.None, interactive: true);

        Assert.False(report.Ok);
        Assert.Contains("not available", report.Error);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task ACommandThatCouldNotStart_IsAnError_NotARecordedRun()
    {
        _next = new CommandResult { Launched = false, LaunchError = "no such file" };

        var report = await Run(Service());

        Assert.False(report.Ok);
        Assert.Contains("no such file", report.Error);
    }

    [Fact]
    public async Task InteractiveAndElevated_AsksTheBrokerForAVisibleRun_WithTheRepoRelativePath()
    {
        (string Root, string Script, IReadOnlyList<string> Args)? seen = null;
        var service = new ScriptRunService(
            _tracker,
            new Probe(Pwsh),
            () => _choices,
            (_, _) => throw new InvalidOperationException("the captured runner must not be used"),
            null,
            (root, script, args, _) =>
            {
                seen = (root, script, args);
                return Task.FromResult(new ElevRunResult(0, "transcript text"));
            });

        var report = await service.RunAsync(_root, @"scripts\a.ps1", ["x"], new ScriptWatchOptions(), CancellationToken.None, interactive: true, elevated: true);

        Assert.True(report.Ok);
        Assert.True(report.Interactive);
        Assert.Equal("transcript text", report.StdOut);
        Assert.Equal((_root, "scripts/a.ps1"), (seen!.Value.Root, seen.Value.Script));
        Assert.Equal(["x"], seen.Value.Args);
    }

    [Fact]
    public async Task InteractiveAndElevated_ABrokerRefusalOrFailure_IsAFailedRun_AndCountsInTheTracker()
    {
        var service = new ScriptRunService(
            _tracker,
            new Probe(Pwsh),
            () => _choices,
            (_, _) => throw new InvalidOperationException("unused"),
            null,
            (_, _, _, _) => Task.FromResult(new ElevRunResult(1, "refused: script is not in the allowlist")));

        var first = await service.RunAsync(_root, @"scripts\a.ps1", [], new ScriptWatchOptions(), CancellationToken.None, interactive: true, elevated: true);
        await service.RunAsync(_root, @"scripts\a.ps1", [], new ScriptWatchOptions(), CancellationToken.None, interactive: true, elevated: true);
        var third = await service.RunAsync(_root, @"scripts\a.ps1", [], new ScriptWatchOptions(), CancellationToken.None, interactive: true, elevated: true);

        Assert.False(first.Ok);
        Assert.Contains("allowlist", first.Error);
        Assert.Equal(ScriptFlag.Repeating, third.Flag);
    }

    [Fact]
    public async Task InteractiveAndElevated_IsPowerShellOnly_AndNeedsABroker()
    {
        var withBroker = new ScriptRunService(_tracker, new Probe(Pwsh, @"C:\Program Files\Git\bin\bash.exe"), () => _choices, (_, _) => throw new InvalidOperationException("unused"), null,
            (_, _, _, _) => throw new InvalidOperationException("must not be asked for a shell script"));

        var shell = await withBroker.RunAsync(_root, @"scripts\a.sh", [], new ScriptWatchOptions(), CancellationToken.None, interactive: true, elevated: true);
        var none = await Service().RunAsync(_root, @"scripts\a.ps1", [], new ScriptWatchOptions(), CancellationToken.None, interactive: true, elevated: true);

        Assert.Contains("PowerShell", shell.Error);
        Assert.Contains("not available", none.Error);
    }
}
