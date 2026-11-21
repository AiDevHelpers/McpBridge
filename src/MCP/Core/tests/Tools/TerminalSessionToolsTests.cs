using AiDevHelpers.MCP.Core.Tools;
using AiDevHelpers.Helpers.TerminalControl.Contracts;
using AiDevHelpers.Helpers.TerminalControl.Models;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class TerminalSessionToolsTests
{
    /// <summary>Fails the test if any path reaches the manager; validation must return before touching it.</summary>
    private sealed class UnreachableManager : ITerminalSessionManager
    {
        public Task InitializeAsync(CancellationToken ct = default) => throw new InvalidOperationException("unexpected");
        public TerminalConfiguration TerminalConfiguration => throw new InvalidOperationException("unexpected");
        public Task<ITerminalSession> CreateSessionAsync(TerminalSessionConfig config, CancellationToken ct = default) => throw new InvalidOperationException("unexpected");
        public ITerminalSession? GetSession(string sessionId) => throw new InvalidOperationException("unexpected");
        public IReadOnlyList<ITerminalSession> GetActiveSessions() => throw new InvalidOperationException("unexpected");
        public Task CloseSessionAsync(string sessionId, bool graceful = true, CancellationToken ct = default) => throw new InvalidOperationException("unexpected");
        public Task ShutdownAsync(CancellationToken ct = default) => throw new InvalidOperationException("unexpected");
    }

    /// <summary>Captures the config passed to CreateSessionAsync, then stops the open.</summary>
    private sealed class CapturingManager : ITerminalSessionManager
    {
        public TerminalSessionConfig? Captured { get; private set; }
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public TerminalConfiguration TerminalConfiguration => throw new InvalidOperationException("unexpected");
        public Task<ITerminalSession> CreateSessionAsync(TerminalSessionConfig config, CancellationToken ct = default)
        {
            Captured = config;
            throw new InvalidOperationException("captured");
        }
        public ITerminalSession? GetSession(string sessionId) => throw new InvalidOperationException("unexpected");
        public IReadOnlyList<ITerminalSession> GetActiveSessions() => throw new InvalidOperationException("unexpected");
        public Task CloseSessionAsync(string sessionId, bool graceful = true, CancellationToken ct = default) => throw new InvalidOperationException("unexpected");
        public Task ShutdownAsync(CancellationToken ct = default) => throw new InvalidOperationException("unexpected");
    }

    [Fact]
    public async Task Open_Elevated_PassesIsElevatedToSessionConfig()
    {
        var manager = new CapturingManager();
        var tools = new TerminalSessionTools(manager);

        await tools.TerminalSession(action: "open", elevated: true);

        Assert.True(manager.Captured?.IsElevated);
    }

    [Fact]
    public async Task Open_Default_IsNotElevated()
    {
        var manager = new CapturingManager();
        var tools = new TerminalSessionTools(manager);

        await tools.TerminalSession(action: "open");

        Assert.False(manager.Captured?.IsElevated);
    }

    [Theory]
    [InlineData(null, TerminalLaunchMode.InteractiveUser)]
    [InlineData("", TerminalLaunchMode.InteractiveUser)]
    [InlineData("InteractiveUser", TerminalLaunchMode.InteractiveUser)]
    [InlineData("serviceaccount", TerminalLaunchMode.ServiceAccount)]
    public void TryParseMode_AcceptsKnownModes(string? value, TerminalLaunchMode expected)
    {
        Assert.True(TerminalSessionTools.TryParseMode(value, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("3")]
    public void TryParseMode_RejectsUnknownModes(string value)
    {
        Assert.False(TerminalSessionTools.TryParseMode(value, out _));
    }

    [Fact]
    public async Task Open_WithInvalidLaunchMode_ReturnsErrorWithoutTouchingManager()
    {
        var tools = new TerminalSessionTools(new UnreachableManager());

        var result = await tools.TerminalSession(action: "open", launchMode: "bogus");

        Assert.StartsWith("Error: launchMode must be", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownAction_ReturnsError()
    {
        var tools = new TerminalSessionTools(new UnreachableManager());

        var result = await tools.TerminalSession(action: "explode");

        Assert.StartsWith("Error: action must be", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exec_WithoutRequiredArguments_ReturnsErrorWithoutTouchingManager()
    {
        var tools = new TerminalSessionTools(new UnreachableManager());

        var result = await tools.TerminalSession(action: "exec", sessionId: "s1");

        Assert.StartsWith("Error: exec needs", result, StringComparison.Ordinal);
    }
}
