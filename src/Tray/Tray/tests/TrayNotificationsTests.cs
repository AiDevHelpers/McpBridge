using Xunit;

namespace AiDevHelpers.Tray.Tests;

public sealed class TrayNotificationsTests
{
    [Fact]
    public void AChangeToADisconnectedStateSaysSo()
    {
        var text = TrayNotifications.For(TrayIconState.Running, TrayIconState.Disconnected);

        Assert.Equal("MCP server not reachable", text);
    }

    [Fact]
    public void ARecoveryToRunningSaysSo()
    {
        Assert.Equal("MCP server running", TrayNotifications.For(TrayIconState.Disconnected, TrayIconState.Running));
    }

    [Fact]
    public void NoChangeAndNoPreviousStateSayNothing()
    {
        Assert.Null(TrayNotifications.For(TrayIconState.Running, TrayIconState.Running));
        Assert.Null(TrayNotifications.For(null, TrayIconState.Stopped));
    }
}

