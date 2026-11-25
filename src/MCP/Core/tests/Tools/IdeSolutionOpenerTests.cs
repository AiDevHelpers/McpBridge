using System.Text.Json;
using AiDevHelpers.Bridge;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class IdeSolutionOpenerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "opener-" + Guid.NewGuid().ToString("N"));
    private readonly string _sln;

    public IdeSolutionOpenerTests()
    {
        Directory.CreateDirectory(_dir);
        _sln = Path.Combine(_dir, "App.sln");
        File.WriteAllText(_sln, string.Empty);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class FakeLauncher(bool broker, string? devenvError = null) : ISolutionLauncher
    {
        public int DevenvCalls { get; private set; }

        public string? LaunchDevenv(string solutionPath, out int? pid)
        {
            DevenvCalls++;
            pid = null; // no pid, so no starting marker is written in tests
            return devenvError;
        }

        public Task<(bool Used, string? Error)> TryBrokerLaunchAsync(string solutionPath, CancellationToken ct) =>
            Task.FromResult((broker, (string?)null));
    }

    private static VsInstance Instance(string sln) => new(4242, "AiDevHelpers.VS.4242", sln, DateTime.UtcNow, "VisualStudio", []);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task MissingSolution_ReportsErrorWithoutLaunching()
    {
        var launcher = new FakeLauncher(broker: false);
        var opener = new IdeSolutionOpener(launcher, _ => null, _ => true, (d, ct) => Task.CompletedTask, () => DateTime.UtcNow, TimeSpan.FromSeconds(1));

        var json = Parse(await opener.OpenAsync(Path.Combine(_dir, "Missing.sln"), CancellationToken.None));

        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal(0, launcher.DevenvCalls);
    }

    [Fact]
    public async Task RunningInstance_IsReusedWithoutLaunching()
    {
        var launcher = new FakeLauncher(broker: false);
        var opener = new IdeSolutionOpener(launcher, _ => Instance(_sln), _ => true, (d, ct) => Task.CompletedTask, () => DateTime.UtcNow, TimeSpan.FromSeconds(1));

        var json = Parse(await opener.OpenAsync(_sln, CancellationToken.None));

        Assert.True(json.GetProperty("already_running").GetBoolean());
        Assert.Equal(0, launcher.DevenvCalls);
    }

    [Fact]
    public async Task BrokerLaunch_SkipsDevenvAndWaitsForTheBridge()
    {
        var launcher = new FakeLauncher(broker: true);
        var polls = 0;
        var opener = new IdeSolutionOpener(
            launcher,
            _ => ++polls >= 2 ? Instance(_sln) : null,
            _ => true,
            (d, ct) => Task.CompletedTask,
            () => DateTime.UtcNow,
            TimeSpan.FromSeconds(30));

        var json = Parse(await opener.OpenAsync(_sln, CancellationToken.None));

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.True(json.GetProperty("bridge_ready").GetBoolean());
        Assert.Equal(0, launcher.DevenvCalls);
    }

    [Fact]
    public async Task BridgeThatNeverAnswers_TimesOutAsNotReady()
    {
        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var launcher = new FakeLauncher(broker: false);
        var opener = new IdeSolutionOpener(
            launcher,
            _ => null,
            _ => false,
            (d, ct) => { clock = clock.Add(d); return Task.CompletedTask; },
            () => clock,
            TimeSpan.FromSeconds(2));

        var json = Parse(await opener.OpenAsync(_sln, CancellationToken.None));

        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.False(json.GetProperty("bridge_ready").GetBoolean());
        Assert.Equal(1, launcher.DevenvCalls);
    }

    [Fact]
    public async Task CancelledWait_StopsThePolling()
    {
        using var cts = new CancellationTokenSource();
        var opener = new IdeSolutionOpener(
            new FakeLauncher(broker: false),
            _ => null,
            _ => false,
            (d, ct) => { cts.Cancel(); return Task.FromCanceled(ct); },
            () => DateTime.UtcNow,
            TimeSpan.FromSeconds(60));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opener.OpenAsync(_sln, cts.Token));
    }
}
