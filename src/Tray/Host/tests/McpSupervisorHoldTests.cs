using System.Diagnostics;
using AiDevHelpers.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiDevHelpers.Host.Tests;

public sealed class McpSupervisorHoldTests
{
    // Starts a real long-running process (ping) so the supervisor has something to hold and kill.
    private sealed class FakeLauncher : ISessionLauncher
    {
        public List<Process> Started { get; } = [];

        public Process? Start(uint session, string exe, string arguments, string workingDirectory)
        {
            Process process = Process.Start(new ProcessStartInfo("ping", "-n 60 127.0.0.1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            Started.Add(process);
            return process;
        }
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(50);
        }

        return true;
    }

    private static (McpSupervisor Supervisor, McpHold Hold, FakeLauncher Launcher) Supervisor()
    {
        var hold = new McpHold();
        var launcher = new FakeLauncher();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Mcp:Exe"] = "AiDevHelpers.MCP.Http.exe" }).Build();
        var supervisor = new McpSupervisor(launcher, config, NullLogger<McpSupervisor>.Instance, hold, () => 1u);
        return (supervisor, hold, launcher);
    }

    private static async Task Stop(McpSupervisor supervisor, FakeLauncher launcher)
    {
        await supervisor.StopAsync(CancellationToken.None);
        foreach (Process process in launcher.Started)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    // While an update is applying, MCP.Http must not be started.
    [Fact]
    public async Task HeldProcessStaysDown()
    {
        var (supervisor, hold, launcher) = Supervisor();
        hold.Hold();

        await supervisor.StartAsync(CancellationToken.None);
        await Task.Delay(1500);

        Assert.Empty(launcher.Started);
        await Stop(supervisor, launcher);
    }

    [Fact]
    public async Task ReleaseStartsWithoutBackoff()
    {
        var (supervisor, hold, launcher) = Supervisor();
        hold.Hold();
        await supervisor.StartAsync(CancellationToken.None);

        hold.Release();

        Assert.True(await WaitUntil(() => launcher.Started.Count == 1, TimeSpan.FromSeconds(3)));
        await Stop(supervisor, launcher);
    }

    // A hold stops a running MCP.Http and keeps it down until release.
    [Fact]
    public async Task HoldKillsRunningProcess()
    {
        var (supervisor, hold, launcher) = Supervisor();
        await supervisor.StartAsync(CancellationToken.None);
        Assert.True(await WaitUntil(() => launcher.Started.Count == 1, TimeSpan.FromSeconds(5)));

        hold.Hold();

        Assert.True(await WaitUntil(() => launcher.Started[0].HasExited, TimeSpan.FromSeconds(5)));
        await Task.Delay(1000);
        Assert.Single(launcher.Started);
        await Stop(supervisor, launcher);
    }

    // The stop caused by a hold is not a crash, so after release the process comes back at once.
    [Fact]
    public async Task ReleaseAfterHoldKillRestartsPromptly()
    {
        var (supervisor, hold, launcher) = Supervisor();
        await supervisor.StartAsync(CancellationToken.None);
        Assert.True(await WaitUntil(() => launcher.Started.Count == 1, TimeSpan.FromSeconds(5)));
        hold.Hold();
        Assert.True(await WaitUntil(() => launcher.Started[0].HasExited, TimeSpan.FromSeconds(5)));

        hold.Release();

        Assert.True(await WaitUntil(() => launcher.Started.Count == 2, TimeSpan.FromSeconds(3)));
        await Stop(supervisor, launcher);
    }
}
