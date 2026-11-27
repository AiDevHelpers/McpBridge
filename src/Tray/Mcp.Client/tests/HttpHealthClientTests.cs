using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace AiDevHelpers.Mcp.Client.Tests;

public sealed class HttpHealthClientTests
{
    /// <summary>A one-request HTTP server that answers the given status and body.</summary>
    private static (int Port, Task Served) Serve(int status, string body)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = Task.Run(async () =>
        {
            using (listener.Server)
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[4096];
                await stream.ReadAsync(buffer);
                var response = $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
            }
        });
        return (port, served);
    }

    private static HttpHealthClient Client() => new(TimeSpan.FromSeconds(3));

    [Fact]
    public async Task AHealthyServerReportsItsVersionAndPid()
    {
        var (port, served) = Serve(200, "{\"status\":\"ok\",\"version\":\"1.2.3.4\",\"pid\":4242,\"port\":1}");

        var result = await Client().CheckAsync(port, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("1.2.3.4", result.Version);
        Assert.Equal(4242, result.Pid);
        await served;
    }

    [Fact]
    public async Task AServerErrorIsNotHealthy()
    {
        var (port, served) = Serve(500, "{\"status\":\"ok\",\"version\":\"1\",\"pid\":1,\"port\":1}");

        Assert.False((await Client().CheckAsync(port, CancellationToken.None)).Ok);
        await served;
    }

    [Fact]
    public async Task ABodyThatIsNotHealthJsonIsNotHealthy()
    {
        var (port, served) = Serve(200, "<html>hello</html>");

        Assert.False((await Client().CheckAsync(port, CancellationToken.None)).Ok);
        await served;
    }

    [Fact]
    public async Task NothingListeningIsNotHealthy()
    {
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();

        Assert.False((await Client().CheckAsync(port, CancellationToken.None)).Ok);
    }
}

public sealed class McpPortTests
{
    [Theory]
    [InlineData(null, 5058)]
    [InlineData("", 5058)]
    [InlineData("abc", 5058)]
    [InlineData("0", 5058)]
    [InlineData("65536", 5058)]
    [InlineData("5057", 5057)]
    public void ThePortIsTheSettingOrTheDefault(string? value, int expected) => Assert.Equal(expected, McpPort.Parse(value));
}
