using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AiDevHelpers.MCP.Http.Tests;

public sealed class ToolRiskCatalogTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "risk-" + Guid.NewGuid().ToString("N"));
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

    [Theory]
    [InlineData("terminal_session")]
    [InlineData("script_run")]
    [InlineData("run_elevated_script")]
    [InlineData("ide_kill_instance")]
    [InlineData("ide_kill_process")]
    public void TheToolsTheUserNamedAreDangerous(string tool)
    {
        Assert.Equal(ToolRisk.Dangerous, ToolRiskCatalog.Classify(tool));
    }

    [Theory]
    [InlineData("ide_list_instances")]
    [InlineData("git_status")]
    [InlineData("ide_build")]
    public void ReadOnlyAndEverydayToolsAreSafe(string tool)
    {
        Assert.Equal(ToolRisk.Safe, ToolRiskCatalog.Classify(tool));
    }

    [Theory]
    [InlineData("a_tool_added_later")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnclassifiedToolIsDangerous(string? tool)
    {
        Assert.Equal(ToolRisk.Dangerous, ToolRiskCatalog.Classify(tool));
    }

    [Fact]
    public async Task EveryRegisteredToolIsClassifiedAndEveryCatalogEntryExists()
    {
        _host = await McpHttpHost.StartAsync(new HttpHostOptions(0, Path.Combine(_root, "token"), Path.Combine(_root, "endpoint.json")), CancellationToken.None);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.Port}") };

        var names = await ListToolNamesAsync(client);

        Assert.True(names.Count > 100);
        Assert.Empty(names.Where(n => !ToolRiskCatalog.IsClassified(n)).ToList());
        Assert.Empty(ToolRiskCatalog.AllNames.Where(n => !names.Contains(n)).ToList());
    }

    private async Task<HashSet<string>> ListToolNamesAsync(HttpClient client)
    {
        using var init = Send(client, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}}", null);
        using var initResponse = await client.SendAsync(init);
        var session = initResponse.Headers.GetValues("Mcp-Session-Id").Single();

        using var list = Send(client, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}", session);
        using var listResponse = await client.SendAsync(list);
        var body = await listResponse.Content.ReadAsStringAsync();
        var data = body.Split('\n').First(l => l.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
        return JsonDocument.Parse(data).RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).ToHashSet();
    }

    private HttpRequestMessage Send(HttpClient client, string json, string? session)
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
}
