using System.Reactive.Linq;
using AiDevHelpers.Mcp.Client;
using AiDevHelpers.Tray.Mcp;
using Microsoft.Reactive.Testing;
using Xunit;

namespace AiDevHelpers.Tray.Tests;

public sealed class McpStatusMonitorTests
{
    private const int Port = 5757;
    private const int ServerPid = 4242;

    private sealed class Fakes
    {
        public EndpointReadResult Endpoint { get; set; } = Found();

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
            public Task<HealthResult> CheckAsync(int port, string token, CancellationToken cancellationToken)
            {
                fakes.HealthCalls++;
                return Task.FromResult(fakes.Health);
            }
        }
    }

    private static EndpointReadResult Found(string? error = null, string token = "token") =>
        new(EndpointReadKind.Found, new McpEndpoint(ServerPid, Port, "1.0", error, token));

    private static (McpStatusMonitor Monitor, TestScheduler Scheduler, List<McpStatus> Seen, IDisposable Subscription) Start(Fakes fakes)
    {
        var scheduler = new TestScheduler();
        var monitor = new McpStatusMonitor(() => fakes.Endpoint, fakes.Owner, fakes.Client, TimeSpan.FromSeconds(5), scheduler);
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
    public void NoEndpointRecordIsDisconnected()
    {
        var (_, _, seen, subscription) = Start(new Fakes { Endpoint = new EndpointReadResult(EndpointReadKind.Missing) });
        using var _ = subscription;

        Assert.Equal(new McpStatus(ConnectionState.Disconnected, null, null, null, "no-endpoint"), Assert.Single(seen));
    }

    [Fact]
    public void AnInvalidEndpointRecordIsDisconnected()
    {
        var (_, _, seen, subscription) = Start(new Fakes { Endpoint = new EndpointReadResult(EndpointReadKind.Invalid) });
        using var _ = subscription;

        Assert.Equal("endpoint-invalid", Assert.Single(seen).Problem);
    }

    [Fact]
    public void APortConflictFromTheServerIsShown()
    {
        var (_, _, seen, subscription) = Start(new Fakes { Endpoint = Found(error: "port-in-use") });
        using var _ = subscription;

        var status = Assert.Single(seen);
        Assert.Equal(ConnectionState.Disconnected, status.State);
        Assert.Equal("port-in-use", status.Problem);
    }

    [Fact]
    public void AMissingTokenIsDisconnected()
    {
        var (_, _, seen, subscription) = Start(new Fakes { Endpoint = Found(token: string.Empty) });
        using var _ = subscription;

        Assert.Equal("no-token", Assert.Single(seen).Problem);
    }

    [Fact]
    public void AnotherProcessOnThePortIsRefusedAndNeverGetsTheToken()
    {
        var fakes = new Fakes { ListenerPid = 9999 };
        var (_, _, seen, subscription) = Start(fakes);
        using var _ = subscription;

        Assert.Equal("listener-mismatch", Assert.Single(seen).Problem);
        Assert.Equal(0, fakes.HealthCalls);
    }

    [Fact]
    public void NothingListeningIsDisconnected()
    {
        var fakes = new Fakes { ListenerPid = null };
        var (_, _, seen, subscription) = Start(fakes);
        using var _ = subscription;

        Assert.Equal("not-listening", Assert.Single(seen).Problem);
        Assert.Equal(0, fakes.HealthCalls);
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
