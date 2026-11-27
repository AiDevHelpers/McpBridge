using System.Reactive.Subjects;
using System.Reactive.Concurrency;
using Xunit;

namespace AiDevHelpers.Tray.Tests;

public sealed class StatusViewModelTests
{
    [Fact]
    public void TheIconStartsDisconnectedBeforeAnyReading()
    {
        var vm = new StatusViewModel(new Subject<HealthSnapshot>(), new Subject<ServiceState>(), 5058, Scheduler.Immediate);

        Assert.Equal(TrayIconState.Disconnected, vm.Icon);
    }

    [Fact]
    public void APortConflictIsVisibleAndNamesThePort()
    {
        var health = new Subject<HealthSnapshot>();
        var service = new Subject<ServiceState>();
        var vm = new StatusViewModel(health, service, 5058, Scheduler.Immediate);

        service.OnNext(ServiceState.Running);
        health.OnNext(new HealthSnapshot(TrayConnection.PortConflict, "address already in use"));

        Assert.Equal(TrayIconState.Degraded, vm.Icon);
        Assert.Contains("5058", vm.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSessionCountFromHealthzIsShown()
    {
        var health = new Subject<HealthSnapshot>();
        var service = new Subject<ServiceState>();
        var vm = new StatusViewModel(health, service, 5058, Scheduler.Immediate);

        service.OnNext(ServiceState.Running);
        health.OnNext(new HealthSnapshot(TrayConnection.Connected, "healthz ok", 3));

        Assert.Equal(3, vm.Sessions);
        Assert.Equal(TrayIconState.Running, vm.Icon);
        Assert.Equal("http://127.0.0.1:5058/mcp", vm.Endpoint);
    }

    [Fact]
    public void AStoppedServiceShowsStopped()
    {
        var health = new Subject<HealthSnapshot>();
        var service = new Subject<ServiceState>();
        var vm = new StatusViewModel(health, service, 5058, Scheduler.Immediate);

        health.OnNext(new HealthSnapshot(TrayConnection.Disconnected, "ConnectionRefused"));
        service.OnNext(ServiceState.Stopped);

        Assert.Equal(TrayIconState.Stopped, vm.Icon);
        Assert.Equal("Service stopped", vm.Headline);
    }
}

