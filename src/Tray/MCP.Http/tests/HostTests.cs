using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AiDevHelpers.MCP.Http.Tests;

public sealed class HostTests : IAsyncLifetime
{
    private const string InitializeJson = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcphttp-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousState = Environment.GetEnvironmentVariable("AIDEVHELPERS_CONTROL_STATE");
    private RunningHost? _host;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("AIDEVHELPERS_CONTROL_STATE", Path.Combine(_root, "control.json"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        Environment.SetEnvironmentVariable("AIDEVHELPERS_CONTROL_STATE", _previousState);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private HttpHostOptions Options(int port = 0) =>
        new(port, Path.Combine(_root, "token"), Path.Combine(_root, "endpoint.json"));

    private async Task<HttpClient> StartClientAsync()
    {
        _host = await McpHttpHost.StartAsync(Options(), CancellationToken.None);
        return new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.Port}") };
    }

    private HttpRequestMessage Post(string json, bool withToken = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (withToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host!.Token);
        }

        return request;
    }

    [Fact]
    public async Task ARequestWithoutATokenIsUnauthorized()
    {
        using var client = await StartClientAsync();

        using var response = await client.SendAsync(Post(InitializeJson, withToken: false));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ABadHostHeaderIsForbidden()
    {
        using var client = await StartClientAsync();
        using var request = Post(InitializeJson);
        request.Headers.Host = "evil.example";

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnOriginHeaderIsForbidden()
    {
        using var client = await StartClientAsync();
        using var request = Post(InitializeJson);
        request.Headers.Add("Origin", "http://evil.example");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task HealthzReportsVersionPidAndPort()
    {
        using var client = await StartClientAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host!.Token);

        using var response = await client.SendAsync(request);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.Equal(Environment.ProcessId, json.GetProperty("pid").GetInt32());
        Assert.Equal(_host.Port, json.GetProperty("port").GetInt32());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task TwoClientsGetDistinctSessions()
    {
        using var client = await StartClientAsync();

        using var first = await client.SendAsync(Post(InitializeJson));
        using var second = await client.SendAsync(Post(InitializeJson));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var a = first.Headers.GetValues("Mcp-Session-Id").Single();
        var b = second.Headers.GetValues("Mcp-Session-Id").Single();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task AnUnknownSessionIsNotFound()
    {
        using var client = await StartClientAsync();
        using var request = Post("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
        request.Headers.Add("Mcp-Session-Id", "bogus");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheEndpointFileRecordsPidPortAndVersion()
    {
        using var client = await StartClientAsync();

        var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "endpoint.json"))).RootElement;

        Assert.Equal(Environment.ProcessId, json.GetProperty("pid").GetInt32());
        Assert.Equal(_host!.Port, json.GetProperty("port").GetInt32());
        Assert.False(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task APortInUseIsReportedInTheEndpointFile()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;

        await Assert.ThrowsAsync<PortInUseException>(() => McpHttpHost.StartAsync(Options(port), CancellationToken.None));

        var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "endpoint.json"))).RootElement;
        Assert.Equal("port-in-use", json.GetProperty("error").GetString());
        Assert.Equal(port, json.GetProperty("port").GetInt32());
    }
}
