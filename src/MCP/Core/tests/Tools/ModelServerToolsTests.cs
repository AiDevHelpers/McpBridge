using System.Net;
using System.Text;
using System.Text.Json;
using AiDevHelpers.Helpers.ModelServerHelper;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

[Collection("ModelServerTools")]
public sealed class ModelServerToolsTests : IDisposable
{
    public void Dispose() => ModelServerTools.ClientFactory = null;

    private sealed class Handler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request.RequestUri!.AbsolutePath));
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static ModelServerTools Tools() =>
        new(Microsoft.Extensions.Options.Options.Create(new ModelServerOptions()));

    private static void Use(Func<string, HttpResponseMessage> respond) =>
        ModelServerTools.ClientFactory = () => new ModelServerClient(
            new HttpClient(new Handler(respond)), new Uri("http://localhost:11435"), TimeProvider.System, TimeSpan.FromSeconds(2));

    [Fact]
    public async Task Status_running_reports_models()
    {
        Use(p => p == "/api/tags" ? Json("{\"models\":[{\"name\":\"HelperModel\"}]}") : Json("{\"models\":[]}"));

        using var doc = JsonDocument.Parse(await Tools().ModelServerStatus());
        var root = doc.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.True(root.GetProperty("running").GetBoolean());
        Assert.True(root.GetProperty("healthy").GetBoolean());
        Assert.Equal("HelperModel", root.GetProperty("models")[0].GetString());
        Assert.Equal("idle", root.GetProperty("vramState").GetString());
    }

    [Fact]
    public async Task Status_unreachable_is_ok_but_not_running()
    {
        Use(_ => throw new HttpRequestException("refused"));

        using var doc = JsonDocument.Parse(await Tools().ModelServerStatus());

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("running").GetBoolean());
    }

    [Fact]
    public async Task Capacity_returns_values()
    {
        Use(_ => Json("{\"cpuThreads\":4,\"gpu\":{\"detected\":false},\"maxConcurrentSummaryRequests\":2}"));

        using var doc = JsonDocument.Parse(await Tools().ModelServerCapacity());
        var root = doc.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(2, root.GetProperty("maxConcurrentSummaryRequests").GetInt32());
        Assert.Equal(4, root.GetProperty("cpuThreads").GetInt32());
        Assert.False(root.GetProperty("fromFallback").GetBoolean());
    }

    [Fact]
    public async Task Capacity_unreachable_uses_fallback()
    {
        Use(_ => throw new HttpRequestException("refused"));

        using var doc = JsonDocument.Parse(await Tools().ModelServerCapacity(refresh: true));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("fromFallback").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("maxConcurrentSummaryRequests").GetInt32());
    }
}
