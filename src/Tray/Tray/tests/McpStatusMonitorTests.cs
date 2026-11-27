using System.Reactive.Linq;
using AiDevHelpers.Mcp.Client;
using AiDevHelpers.Tray.Mcp;
using Microsoft.Reactive.Testing;
using Xunit;

namespace AiDevHelpers.Tray.Tests;

public sealed class McpStatusMonitorTests
{
    private const int Port = 5058;
    private const int ServerPid = 4242;

    private sealed class Fakes
    {
        public int? ListenerPid { get; set; } = ServerPid;

        public HealthResult Health { get; set; } = new(true, "1.0", ServerPid);

        public int HealthCalls { get; private set; }

        public IListenerOwner Owner => new FakeOwner(this);

        public IHealthClient Client => new FakeClient(this);

        private sealed class FakeOwner(Fakes fakes) : IListenerOwner
        {
            public int? OwnerPid(int port) => fakes.ListenerPid;
        }

        private sealed class FakeClient(Fakes fakes) : IHealthClient
        {
            public Task<HealthResult> CheckAsync(int port, CancellationToken cancellationToken)
            {
                fakes.HealthCalls++;
                return Task.FromResult(fakes.Health);
            }
        }
    }

    private static (McpStatusMonitor Monitor, TestScheduler Scheduler, List<McpStatus> Seen, IDisposable Subscription) Start(Fakes fakes)
    {
        var scheduler = new TestScheduler();
        var monitor = new McpStatusMonitor(Port, fakes.Owner, fakes.Client, TimeSpan.FromSeconds(5), scheduler);
        var seen = new List<McpStatus>();
        var subscription = monitor.Status.Subscribe(seen.Add);
        scheduler.AdvanceBy(1);
        return (monitor, scheduler, seen, subscription);
    }

    private static void Tick(TestScheduler scheduler, int times = 1) => scheduler.AdvanceBy(TimeSpan.FromSeconds(5 * times).Ticks);

    [Fact]
    public void AHealthyServerThatOwnsItsPortIsConnected()
    {
        var (_, _, seen, subscription) = Start(new Fakes());
        using var _ = subscription;

        var status = Assert.Single(seen);
        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Equal("1.0", status.Version);
        Assert.Equal(Port, status.Port);
        Assert.Null(status.Problem);
    }

    [Fact]
    public void NothingListeningIsDisconnectedWithoutAHealthCall()
    {
        var fakes = new Fakes { ListenerPid = null };
        var (_, _, seen, subscription) = Start(fakes);
        using var _ = subscription;

        Assert.Equal("not-listening", Assert.Single(seen).Problem);
        Assert.Equal(0, fakes.HealthCalls);
    }

    [Fact]
    public void AnotherProcessOnThePortIsRefused()
    {
        var (_, _, seen, subscription) = Start(new Fakes { ListenerPid = 9999 });
        using var _ = subscription;

        Assert.Equal("listener-mismatch", Assert.Single(seen).Problem);
    }

    [Fact]
    public void AHealthAnswerFromAnotherPidIsRefused()
    {
        var (_, _, seen, subscription) = Start(new Fakes { Health = new HealthResult(true, "1.0", 1) });
        using var _ = subscription;

        Assert.Equal("listener-mismatch", Assert.Single(seen).Problem);
    }

    [Fact]
    public void AServerThatStopsAnsweringIsReconnectingThenDisconnectedThenConnectedAgain()
    {
        var fakes = new Fakes();
        var (_, scheduler, seen, subscription) = Start(fakes);
        using var _ = subscription;

        fakes.Health = new HealthResult(false);
        Tick(scheduler);
        Tick(scheduler);
        Tick(scheduler);
        fakes.Health = new HealthResult(true, "1.0", ServerPid);
        Tick(scheduler);

        Assert.Equal(
            [ConnectionState.Connected, ConnectionState.Reconnecting, ConnectionState.Disconnected, ConnectionState.Connected],
            seen.Select(s => s.State).ToArray());
        Assert.Equal("unhealthy", seen[2].Problem);
    }

    [Fact]
    public void RepeatedIdenticalStatusesAreNotEmittedAgain()
    {
        var (_, scheduler, seen, subscription) = Start(new Fakes());
        using var _ = subscription;

        Tick(scheduler, 3);

        Assert.Single(seen);
    }

    [Fact]
    public void ALateSubscriberGetsTheCurrentStatusImmediately()
    {
        var (monitor, scheduler, _, subscription) = Start(new Fakes());
        using var _ = subscription;
        Tick(scheduler);

        McpStatus? late = null;
        using var lateSubscription = monitor.Status.Subscribe(s => late = s);

        Assert.Equal(ConnectionState.Connected, late!.State);
    }

    [Fact]
    public void PollingStopsWhenTheLastSubscriberLeaves()
    {
        var fakes = new Fakes();
        var (_, scheduler, _, subscription) = Start(fakes);
        Tick(scheduler);
        subscription.Dispose();
        var calls = fakes.HealthCalls;

        Tick(scheduler, 5);

        Assert.Equal(calls, fakes.HealthCalls);
    }
}
