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

public sealed class HttpHealthProbeTests
{
    /// <summary>A one-request HTTP server that answers /healthz with the given status and body.</summary>
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

    [Fact]
    public async Task HealthyWhenHealthzAnswersOkOnThePort()
    {
        var (port, served) = Serve(200, "{\"status\":\"ok\",\"version\":\"1\",\"pid\":1,\"port\":1}");

        Assert.True(await new HttpHealthProbe(port, TimeSpan.FromSeconds(3)).IsHealthyAsync(CancellationToken.None));
        await served;
    }

    [Fact]
    public async Task UnhealthyWhenTheServerAnswersAnError()
    {
        var (port, served) = Serve(500, "{\"status\":\"ok\",\"version\":\"1\",\"pid\":1,\"port\":1}");

        Assert.False(await new HttpHealthProbe(port, TimeSpan.FromSeconds(3)).IsHealthyAsync(CancellationToken.None));
        await served;
    }

    [Fact]
    public async Task UnhealthyWhenNothingListens()
    {
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();

        Assert.False(await new HttpHealthProbe(port, TimeSpan.FromSeconds(3)).IsHealthyAsync(CancellationToken.None));
    }
}
