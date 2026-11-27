using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace AiDevHelpers.Host.Tests;

public sealed class ControlFileTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "control-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Theory]
    [InlineData("{\"state\":\"closing\"}", true)]
    [InlineData("{\"state\":\"paused\"}", false)]
    [InlineData("{\"state\":\"running\"}", false)]
    [InlineData("not json", false)]
    [InlineData("{}", false)]
    public void OnlyTheClosingStateHoldsBackTheServer(string content, bool expected)
    {
        File.WriteAllText(_path, content);

        Assert.Equal(expected, ControlFile.IsClosing(_path));
    }

    [Fact]
    public void AMissingFileIsNotClosing()
    {
        Assert.False(ControlFile.IsClosing(_path));
    }
}

public sealed class HttpHealthProbeTests : IDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "probe-" + Guid.NewGuid().ToString("N"));

    public HttpHealthProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string TokenPath => Path.Combine(_root, "token");

    private string EndpointPath => Path.Combine(_root, "endpoint.json");

    private HttpHealthProbe Probe() => new(TokenPath, EndpointPath, TimeSpan.FromSeconds(3));

    /// <summary>A one-request HTTP server that answers /healthz with the given status when the bearer token matches.</summary>
    private static (int Port, Task Served) Serve(string expectedToken, int statusWhenAuthorized)
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
                var authorized = request.Contains("Authorization: Bearer " + expectedToken, StringComparison.OrdinalIgnoreCase);
                var status = authorized ? statusWhenAuthorized : 401;
                var body = authorized ? "{\"status\":\"ok\",\"version\":\"1\",\"pid\":1,\"port\":1}" : string.Empty;
                var response = $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            }
        });
        return (port, served);
    }

    [Fact]
    public async Task HealthyWhenHealthzAnswersOkForTheToken()
    {
        File.WriteAllText(TokenPath, Token);
        var (port, served) = Serve(Token, 200);
        File.WriteAllText(EndpointPath, $"{{\"pid\":1,\"port\":{port},\"version\":\"1\"}}");

        Assert.True(await Probe().IsHealthyAsync(CancellationToken.None));
        await served;
    }

    [Fact]
    public async Task UnhealthyWhenTheServerRefusesTheToken()
    {
        File.WriteAllText(TokenPath, "a-different-token-a-different-token");
        var (port, served) = Serve(Token, 200);
        File.WriteAllText(EndpointPath, $"{{\"pid\":1,\"port\":{port},\"version\":\"1\"}}");

        Assert.False(await Probe().IsHealthyAsync(CancellationToken.None));
        await served;
    }

    [Fact]
    public async Task UnhealthyWhenThereIsNoEndpointRecord()
    {
        File.WriteAllText(TokenPath, Token);

        Assert.False(await Probe().IsHealthyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UnhealthyWhenTheEndpointRecordReportsAnError()
    {
        File.WriteAllText(TokenPath, Token);
        File.WriteAllText(EndpointPath, "{\"pid\":1,\"port\":5757,\"version\":\"1\",\"error\":\"port-in-use\"}");

        Assert.False(await Probe().IsHealthyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UnhealthyWhenNothingListens()
    {
        File.WriteAllText(TokenPath, Token);
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        File.WriteAllText(EndpointPath, $"{{\"pid\":1,\"port\":{port},\"version\":\"1\"}}");

        Assert.False(await Probe().IsHealthyAsync(CancellationToken.None));
    }
}
