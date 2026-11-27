using System.Runtime.Versioning;
using AiDevHelpers.Helpers.WindowsSession;
using Xunit;

namespace AiDevHelpers.Host.Tests;

[SupportedOSPlatform("windows")]
public sealed class McpSupervisorTests
{
    private sealed class FakeSessions : ISessionSource
    {
        public uint Console { get; set; } = UserSession.NoSession;

        public HashSet<uint> WithUser { get; } = [];

        public uint FirstLoggedOn { get; set; } = UserSession.NoSession;

        public uint ConsoleSession => Console;

        public bool HasUser(uint session) => WithUser.Contains(session);

        public uint FirstLoggedOnSession() => FirstLoggedOn;
    }

    private sealed class FakeChild(int id) : IChildProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id { get; } = id;

        public bool HasExited => _exit.Task.IsCompleted;

        public bool Killed { get; private set; }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

        public void Kill()
        {
            Killed = true;
            _exit.TrySetResult();
        }

        public void Exit() => _exit.TrySetResult();

        public void Dispose()
        {
        }
    }

    private sealed class FakeLauncher : ISessionLauncher
    {
        public List<(string File, string Args, uint Session)> Launches { get; } = [];

        public List<FakeChild> Children { get; } = [];

        public IChildProcess? Start(string fileName, string arguments, uint sessionId, string workingDirectory)
        {
            Launches.Add((fileName, arguments, sessionId));
            var child = new FakeChild(100 + Children.Count);
            Children.Add(child);
            return child;
        }
    }

    private sealed class FakeHealth : IHealthProbe
    {
        public bool Healthy { get; set; } = true;

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Task.FromResult(Healthy);
    }

    /// <summary>Completes delays immediately, records them, and lets a test run code between delays.</summary>
    private sealed class FakeDelay : IDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Action<int>? OnDelay { get; set; }

        public async Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            Delays.Add(duration);
            OnDelay?.Invoke(Delays.Count);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static readonly SupervisorOptions Options = new("C:\\app\\AiDevHelpers.MCP.Http.exe", "--port 5757", "C:\\app")
    {
        PollInterval = TimeSpan.FromMilliseconds(100),
        HealthGracePolls = 1,
        UnhealthyLimit = 2,
    };

    private static async Task RunUntilAsync(McpSupervisor supervisor, Func<bool> done, TimeSpan? limit = null)
    {
        using var cts = new CancellationTokenSource(limit ?? TimeSpan.FromSeconds(10));
        var run = supervisor.RunAsync(cts.Token);
        while (!done() && !cts.IsCancellationRequested)
        {
            await Task.Delay(5);
        }

        await cts.CancelAsync();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task NothingStartsWhileNoOneIsLoggedOn()
    {
        var sessions = new FakeSessions();
        var launcher = new FakeLauncher();
        var supervisor = new McpSupervisor(Options, sessions, launcher, new FakeHealth(), () => false, new FakeDelay());

        var sawNoUserSession = false;
        await RunUntilAsync(supervisor, () => sawNoUserSession |= supervisor.State == SupervisorState.NoUserSession);

        Assert.True(sawNoUserSession);
        Assert.Empty(launcher.Launches);
        Assert.Equal(SupervisorState.Stopped, supervisor.State);
    }

    [Fact]
    public async Task StartsTheServerInTheConsoleSessionWhenItHasAUser()
    {
        var sessions = new FakeSessions { Console = 1 };
        sessions.WithUser.Add(1);
        var launcher = new FakeLauncher();
        var supervisor = new McpSupervisor(Options, sessions, launcher, new FakeHealth(), () => false, new FakeDelay());

        await RunUntilAsync(supervisor, () => launcher.Launches.Count >= 1);

        Assert.Equal(("C:\\app\\AiDevHelpers.MCP.Http.exe", "--port 5757", 1u), launcher.Launches[0]);
    }

    [Fact]
    public async Task StartsTheServerInAnRdpSessionWhenTheConsoleIsEmpty()
    {
        var sessions = new FakeSessions { Console = 1, FirstLoggedOn = 3 };
        var launcher = new FakeLauncher();
        var supervisor = new McpSupervisor(Options, sessions, launcher, new FakeHealth(), () => false, new FakeDelay());

        await RunUntilAsync(supervisor, () => launcher.Launches.Count >= 1);

        Assert.Equal(3u, launcher.Launches[0].Session);
    }

    [Fact]
    public async Task StartsWhenAUserLogsOnLater()
    {
        var sessions = new FakeSessions();
        var launcher = new FakeLauncher();
        var delay = new FakeDelay { OnDelay = n => { if (n == 3) { sessions.FirstLoggedOn = 4; } } };
        var supervisor = new McpSupervisor(Options, sessions, launcher, new FakeHealth(), () => false, delay);

        await RunUntilAsync(supervisor, () => launcher.Launches.Count >= 1);

        Assert.Equal(4u, launcher.Launches[0].Session);
    }

    [Fact]
    public async Task RestartsAfterACrashWithGrowingDelays()
    {
        var sessions = new FakeSessions { Console = 1 };
        sessions.WithUser.Add(1);
        var launcher = new FakeLauncher();
        var delay = new FakeDelay();
        var supervisor = new McpSupervisor(Options, sessions, launcher, new FakeHealth(), () => false, delay);
        var crashed = 0;
        delay.OnDelay = _ =>
        {
            foreach (var child in launcher.Children.Where(c => !c.HasExited))
            {
                child.Exit();
                crashed++;
            }
        };

        await RunUntilAsync(supervisor, () => launcher.Launches.Count >= 3);

        var backoffs = delay.Delays.Where(d => d >= TimeSpan.FromSeconds(1)).Take(2).ToList();
        Assert.Equal(TimeSpan.FromSeconds(1), backoffs[0]);
        Assert.Equal(TimeSpan.FromSeconds(2), backoffs[1]);
    }

    [Fact]
    public async Task MovesTheServerWhenTheUserSessionChanges()
    {
        var sessions = new FakeSessions { Console = 1 };
        sessions.WithUser.Add(1);
        var launcher = new FakeLauncher();
        var supervisor = new McpSupervisor(Options, sessions, launcher, new FakeHealth(), () => false, new FakeDelay());

        await RunUntilAsync(supervisor, () =>
        {
            if (launcher.Launches.Count == 1)
            {
                sessions.Console = UserSession.NoSession;
                sessions.WithUser.Clear();
                sessions.FirstLoggedOn = 5;
            }

            return launcher.Launches.Count >= 2;
        });

        Assert.True(launcher.Children[0].Killed);
        Assert.Equal(5u, launcher.Launches[1].Session);
    }

    [Fact]
    public async Task RestartsAServerThatStopsAnsweringHealthChecks()
    {
        var sessions = new FakeSessions { Console = 1 };
        sessions.WithUser.Add(1);
        var launcher = new FakeLauncher();
        var health = new FakeHealth();
        var supervisor = new McpSupervisor(Options, sessions, launcher, health, () => false, new FakeDelay());

        await RunUntilAsync(supervisor, () =>
        {
            if (launcher.Launches.Count == 1)
            {
                health.Healthy = false;
            }

            if (launcher.Launches.Count >= 2)
            {
                health.Healthy = true;
            }

            return launcher.Launches.Count >= 2;
        });

        Assert.True(launcher.Children[0].Killed);
    }

    [Fact]
    public async Task DoesNotStartWhileTheServersAreClosingForAnUpdate()
    {
        var sessions = new FakeSessions { Console = 1 };
        sessions.WithUser.Add(1);
        var launcher = new FakeLauncher();
        var closing = true;
        var delay = new FakeDelay { OnDelay = n => { if (n >= 4) { closing = false; } } };
        var supervisor = new McpSupervisor(Options, sessions, launcher, new FakeHealth(), () => closing, delay);

        await RunUntilAsync(supervisor, () => launcher.Launches.Count >= 1);

        Assert.True(delay.Delays.Count >= 4);
        Assert.Single(launcher.Launches);
    }

    [Fact]
    public async Task StoppingTheSupervisorStopsTheServer()
    {
        var sessions = new FakeSessions { Console = 1 };
        sessions.WithUser.Add(1);
        var launcher = new FakeLauncher();
        var supervisor = new McpSupervisor(Options, sessions, launcher, new FakeHealth(), () => false, new FakeDelay());

        await RunUntilAsync(supervisor, () => launcher.Launches.Count >= 1);

        Assert.True(launcher.Children[0].Killed);
    }
}
