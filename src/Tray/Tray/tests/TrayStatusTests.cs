using Xunit;

namespace AiDevHelpers.Tray.Tests;

public sealed class TrayStatusTests
{
    [Theory]
    [InlineData(TrayConnection.Connected, ServiceState.Running, TrayIconState.Running)]
    [InlineData(TrayConnection.Connected, ServiceState.Transitioning, TrayIconState.Degraded)]
    [InlineData(TrayConnection.Connected, ServiceState.Stopped, TrayIconState.Degraded)]
    [InlineData(TrayConnection.Disconnected, ServiceState.Running, TrayIconState.Disconnected)]
    [InlineData(TrayConnection.Disconnected, ServiceState.Stopped, TrayIconState.Stopped)]
    [InlineData(TrayConnection.Disconnected, ServiceState.NotInstalled, TrayIconState.Stopped)]
    [InlineData(TrayConnection.PortConflict, ServiceState.Running, TrayIconState.Degraded)]
    public void TheIconFollowsTheServiceAndTheServer(TrayConnection connection, ServiceState service, TrayIconState expected)
    {
        var health = new HealthSnapshot(connection, "detail");

        Assert.Equal(expected, TrayStatus.IconFor(health, service));
    }
}

