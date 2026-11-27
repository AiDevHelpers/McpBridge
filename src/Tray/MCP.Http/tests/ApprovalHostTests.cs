using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AiDevHelpers.MCP.Http.Tests;

public sealed class ApprovalHostTests : IAsyncLifetime
{
    private sealed class CountingChannel(bool approve) : IApprovalChannel
    {
        public int Calls { get; private set; }

        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ApprovalResponse(request.Nonce, request.ArgumentsHash, approve));
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "approval-" + Guid.NewGuid().ToString("N"));
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

    private async Task<(HttpClient Client, string Session)> StartAsync(IApprovalChannel? channel)
    {
        _host = await McpHttpHost.StartAsync(
            new HttpHostOptions(0, Path.Combine(_root, "token"), Path.Combine(_root, "endpoint.json"), channel), CancellationToken.None);
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.Port}") };
        using var init = Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}}", null);
        using var response = await client.SendAsync(init);
        return (client, response.Headers.GetValues("Mcp-Session-Id").Single());
    }

    private HttpRequestMessage Post(string json, string? session)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host!.Token);
        if (session is not null)
        {
            request.Headers.Add("Mcp-Session-Id", session);
        }

        return request;
    }

    private async Task<JsonElement> CallAsync(HttpClient client, string session, string tool, string argumentsJson)
    {
        using var request = Post($"{{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{argumentsJson}}}}}", session);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var data = body.Split('\n').First(l => l.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
        return JsonDocument.Parse(data).RootElement.GetProperty("result").Clone();
    }

    private static string Text(JsonElement result) => result.GetProperty("content")[0].GetProperty("text").GetString()!;

    [Fact]
    public async Task ADangerousCallTheUserDeniesDoesNotRun()
    {
        var channel = new CountingChannel(approve: false);
        var (client, session) = await StartAsync(channel);
        using var _ = client;

        var result = await CallAsync(client, session, "server_control_set", "{\"state\":\"paused\"}");

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal(1, channel.Calls);
        Assert.False(File.Exists(Path.Combine(_root, "control.json")));
    }

    [Fact]
    public async Task ADangerousCallTheUserApprovesRuns()
    {
        var channel = new CountingChannel(approve: true);
        var (client, session) = await StartAsync(channel);
        using var _ = client;

        var result = await CallAsync(client, session, "server_control_set", "{\"state\":\"running\"}");

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        Assert.Contains("\"ok\":true", Text(result));
        Assert.Equal(1, channel.Calls);
    }

    [Fact]
    public async Task ASafeCallRunsWithoutAskingTheTray()
    {
        var channel = new CountingChannel(approve: false);
        var (client, session) = await StartAsync(channel);
        using var _ = client;

        var result = await CallAsync(client, session, "server_control_status", "{}");

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        Assert.Equal(0, channel.Calls);
    }

    [Fact]
    public async Task WithoutATrayADangerousCallIsRefused()
    {
        var (client, session) = await StartAsync(channel: null);
        using var _ = client;

        var result = await CallAsync(client, session, "server_control_set", "{\"state\":\"paused\"}");

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.False(File.Exists(Path.Combine(_root, "control.json")));
    }
}
