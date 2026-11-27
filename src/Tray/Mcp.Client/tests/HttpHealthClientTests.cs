using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace AiDevHelpers.Mcp.Client.Tests;

public sealed class HttpHealthClientTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    /// <summary>A one-request HTTP server: answers with the status and body when the bearer token matches, else 401.</summary>
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
                var read = await stream.ReadAsync(buffer);
                var request = Encoding.ASCII.GetString(buffer, 0, read);
                var authorized = request.Contains("Authorization: Bearer " + Token, StringComparison.OrdinalIgnoreCase);
                var code = authorized ? status : 401;
                var payload = authorized ? body : string.Empty;
                var response = $"HTTP/1.1 {code} X\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(payload)}\r\nConnection: close\r\n\r\n{payload}";
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

        var result = await Client().CheckAsync(port, Token, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("1.2.3.4", result.Version);
        Assert.Equal(4242, result.Pid);
        await served;
    }

    [Fact]
    public async Task ARefusedTokenIsNotHealthy()
    {
        var (port, served) = Serve(200, "{\"status\":\"ok\",\"version\":\"1\",\"pid\":1,\"port\":1}");

        var result = await Client().CheckAsync(port, "another-token-another-token-another", CancellationToken.None);

        Assert.False(result.Ok);
        await served;
    }

    [Fact]
    public async Task ABodyThatIsNotHealthJsonIsNotHealthy()
    {
        var (port, served) = Serve(200, "<html>hello</html>");

        Assert.False((await Client().CheckAsync(port, Token, CancellationToken.None)).Ok);
        await served;
    }

    [Fact]
    public async Task NothingListeningIsNotHealthy()
    {
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();

        Assert.False((await Client().CheckAsync(port, Token, CancellationToken.None)).Ok);
    }
}
