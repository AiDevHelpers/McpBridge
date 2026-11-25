using AiDevHelpers.Common.Terminals;
using AiDevHelpers.Helpers.TerminalControl.Contracts;
using AiDevHelpers.Helpers.TerminalControl.Models;
using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class InteractiveScriptRunnerTests
{
    private sealed class FakeSession(string id) : ITerminalSession
    {
        public string? LastCommand { get; private set; }

        public string SessionId { get; } = id;

        public bool IsElevated => false;

        public int ProcessId => 1;

        public TerminalSessionState State => TerminalSessionState.Ready;

        public TerminalConfiguration Configuration => throw new NotSupportedException();

        public Task InitializeAsync(TerminalSessionConfig config, CancellationToken ct = default) => Task.CompletedTask;

        public Task<TerminalTabInfo> CreateTabAsync(string tabName, string? initialCommand = null, CancellationToken ct = default) =>
            Task.FromResult(new TerminalTabInfo("tab1", tabName, SessionId, DateTime.UtcNow, true));

        public Task<TerminalExecutionResult> ExecuteInTabAsync(string tabId, string command, string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            LastCommand = command;
            return Task.FromResult(new TerminalExecutionResult(true, 2, "out text", "err text"));
        }

        public Task CloseTabAsync(string tabId, CancellationToken ct = default) => Task.CompletedTask;

        public Task TerminateAsync(bool graceful = true, CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<TerminalTabInfo> ListTabsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<TerminalTabInfo?> GetTabAsync(string tabId, CancellationToken ct = default) => Task.FromResult<TerminalTabInfo?>(null);

        public Task<bool> IsAliveAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FakeManager(FakeSession session) : ITerminalSessionManager
    {
        public int Initialized { get; private set; }

        public TerminalSessionConfig? Config { get; private set; }

        public Task InitializeAsync(CancellationToken ct = default)
        {
            Initialized++;
            return Task.CompletedTask;
        }

        public TerminalConfiguration TerminalConfiguration => throw new NotSupportedException();

        public Task<ITerminalSession> CreateSessionAsync(TerminalSessionConfig config, CancellationToken ct = default)
        {
            Config = config;
            return Task.FromResult<ITerminalSession>(session);
        }

        public ITerminalSession? GetSession(string sessionId) => null;

        public IReadOnlyList<ITerminalSession> GetActiveSessions() => [];

        public Task CloseSessionAsync(string sessionId, bool graceful = true, CancellationToken ct = default) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static readonly InteractiveRequest Request = new(@"C:\Program Files\PowerShell\7\pwsh.exe", ["-NoProfile", "-File", @"C:\r\a.ps1", "it's", "x y"], @"C:\r", "a.ps1", TimeSpan.FromMinutes(10));

    [Fact]
    public async Task TheScriptRunsInAVisibleNonElevatedTab_AndTheTabOutputComesBack()
    {
        var session = new FakeSession("s1");
        var manager = new FakeManager(session);

        var result = await new InteractiveScriptRunner(manager).RunAsync(Request, CancellationToken.None);

        Assert.Equal(TerminalLaunchMode.InteractiveUser, manager.Config!.LaunchMode);
        Assert.False(manager.Config.IsElevated);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("out text", result.Stdout);
        Assert.Equal(("s1", "tab1"), (result.SessionId, result.TabId));
    }

    [Fact]
    public async Task TheManagerIsInitializedOnce()
    {
        var manager = new FakeManager(new FakeSession("s1"));
        var runner = new InteractiveScriptRunner(manager);

        await runner.RunAsync(Request, CancellationToken.None);
        await runner.RunAsync(Request, CancellationToken.None);

        Assert.Equal(1, manager.Initialized);
    }

    [Fact]
    public void EveryArgumentIsAQuotedLiteral_IncludingQuotesAndSmartQuotes()
    {
        var command = InteractiveScriptRunner.Command(Request with { Arguments = ["it's", "a\u2019b", "x y", "$(calc)"] });

        Assert.Equal(
            "Set-Location -LiteralPath 'C:\\r'; & 'C:\\Program Files\\PowerShell\\7\\pwsh.exe' 'it''s' 'a\u2019\u2019b' 'x y' '$(calc)'",
            command);
    }

    [Fact]
    public async Task InteractivePowerShellArgumentsLeaveOutNonInteractive()
    {
        var probe = new ProbeWithPwsh();
        var resolved = ScriptInterpreterResolver.Resolve(probe, @"C:\r\a.ps1", null, null, out _)!;

        Assert.Contains("-NonInteractive", resolved.BuildArguments(@"C:\r\a.ps1", []));
        Assert.DoesNotContain("-NonInteractive", resolved.BuildArguments(@"C:\r\a.ps1", [], interactive: true));
        await Task.CompletedTask;
    }

    private sealed class ProbeWithPwsh : ITerminalProbe
    {
        public string? FindOnPath(string fileName) => null;

        public bool FileExists(string path) => path.EndsWith("pwsh.exe", StringComparison.OrdinalIgnoreCase);

        public string? FileVersion(string path) => "7";

        public string? Folder(Environment.SpecialFolder folder) => folder == Environment.SpecialFolder.ProgramFiles ? @"C:\Program Files" : null;

        public string? Run(string fileName, IReadOnlyList<string> arguments, System.Text.Encoding encoding) => null;

        public IReadOnlyList<string> WindowsTerminalSettingsFiles() => [];

        public string? ReadText(string path) => null;
    }
}
