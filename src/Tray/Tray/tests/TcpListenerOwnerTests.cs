using System.Net;
using System.Net.Sockets;
using AiDevHelpers.Tray.Mcp;
using Xunit;

namespace AiDevHelpers.Tray.Tests;

public sealed class TcpListenerOwnerTests
{
    [Fact]
    public void ThePidOfTheProcessListeningOnAPortIsFound()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.Equal(Environment.ProcessId, new TcpListenerOwner().OwnerPid(port));
    }

    [Fact]
    public void APortNobodyListensOnHasNoOwner()
    {
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();

        Assert.Null(new TcpListenerOwner().OwnerPid(port));
    }
}
