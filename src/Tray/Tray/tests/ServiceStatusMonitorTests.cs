using System.Reactive.Linq;
using AiDevHelpers.Tray.Service;
using Microsoft.Reactive.Testing;
using Xunit;

namespace AiDevHelpers.Tray.Tests;

public sealed class ServiceStatusMonitorTests
{
    private sealed class FakeQuery : IServiceQuery
    {
        public ServiceState Next { get; set; } = ServiceState.Running;

        public Exception? Throw { get; set; }

        public int Calls { get; private set; }

        public ServiceState Query(string serviceName)
        {
            Calls++;
            return Throw is null ? Next : throw Throw;
        }
    }

    private static (TestScheduler Scheduler, List<ServiceState> Seen, IDisposable Subscription, ServiceStatusMonitor Monitor) Start(FakeQuery query)
    {
        var scheduler = new TestScheduler();
        var monitor = new ServiceStatusMonitor(query, "AiDevHelpers", TimeSpan.FromSeconds(2), scheduler);
        var seen = new List<ServiceState>();
        var subscription = monitor.State.Subscribe(seen.Add);
        scheduler.AdvanceBy(1);
        return (scheduler, seen, subscription, monitor);
    }

    private static void Tick(TestScheduler scheduler, int times = 1) => scheduler.AdvanceBy(TimeSpan.FromSeconds(2 * times).Ticks);

    [Fact]
    public void TheCurrentStateIsPublishedAtOnceAndEveryChangeAfter()
    {
        var query = new FakeQuery { Next = ServiceState.Stopped };
        var (scheduler, seen, subscription, _) = Start(query);
        using var _ = subscription;

        query.Next = ServiceState.StartPending;
        Tick(scheduler);
        query.Next = ServiceState.Running;
        Tick(scheduler);

        Assert.Equal([ServiceState.Stopped, ServiceState.StartPending, ServiceState.Running], seen.ToArray());
    }

    [Fact]
    public void AnUnchangedStateIsNotRepeated()
    {
        var (scheduler, seen, subscription, _) = Start(new FakeQuery());
        using var _ = subscription;

        Tick(scheduler, 4);

        Assert.Single(seen);
    }

    [Fact]
    public void AFailingQueryBecomesAnUnknownStateInsteadOfAnError()
    {
        var query = new FakeQuery { Throw = new InvalidOperationException("scm unavailable") };
        var (_, seen, subscription, _) = Start(query);
        using var _ = subscription;

        Assert.Equal([ServiceState.Unknown], seen.ToArray());
    }

    [Fact]
    public void ALateSubscriberGetsTheCurrentState()
    {
        var (_, _, subscription, monitor) = Start(new FakeQuery { Next = ServiceState.Stopped });
        using var _ = subscription;

        ServiceState? late = null;
        using var lateSubscription = monitor.State.Subscribe(s => late = s);

        Assert.Equal(ServiceState.Stopped, late);
    }

    [Fact]
    public void QueryingStopsWhenTheLastSubscriberLeaves()
    {
        var query = new FakeQuery();
        var (scheduler, _, subscription, _) = Start(query);
        subscription.Dispose();
        var calls = query.Calls;

        Tick(scheduler, 5);

        Assert.Equal(calls, query.Calls);
    }

    [Fact]
    public void ARealServiceThatDoesNotExistIsNotInstalled()
    {
        Assert.Equal(ServiceState.NotInstalled, new ServiceControllerQuery().Query("AiDevHelpersNoSuchService"));
    }

    [Fact]
    public void ARealInstalledServiceIsFoundWithAKnownState()
    {
        var state = new ServiceControllerQuery().Query("Spooler");

        Assert.NotEqual(ServiceState.NotInstalled, state);
        Assert.NotEqual(ServiceState.Unknown, state);
    }
}
