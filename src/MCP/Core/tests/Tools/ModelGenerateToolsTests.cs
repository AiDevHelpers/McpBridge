using System.Net;
using System.Text;
using System.Text.Json;
using AiDevHelpers.Helpers.ModelServerHelper;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

[Collection("ModelServerTools")]
public sealed class ModelGenerateToolsTests : IDisposable
{
    public void Dispose() => ModelServerTools.GeneratorFactory = null;

    private sealed class Handler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request.RequestUri!.AbsolutePath));
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static void Use(Func<string, HttpResponseMessage> respond) =>
        ModelServerTools.GeneratorFactory = () => new ModelServerGenerator(
            new HttpClient(new Handler(respond)), new Uri("http://localhost:11435"), (_, _) => Task.CompletedTask);

    [Fact]
    public async Task Generate_returns_the_response()
    {
        Use(_ => Json("{\"response\":\"hi there\"}"));

        using var doc = JsonDocument.Parse(await ModelServerTools.ModelGenerate("m", "say hi"));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("hi there", root.GetProperty("response").GetString());
        Assert.False(root.GetProperty("capped").GetBoolean());
    }

    [Fact]
    public async Task Generate_refusal_is_ok_false_with_the_reason()
    {
        Use(_ => Json("{\"response\":\"x\"}"));

        using var doc = JsonDocument.Parse(await ModelServerTools.ModelGenerate("", "p"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Generate_unreachable_router_is_ok_false()
    {
        Use(_ => throw new HttpRequestException("refused"));

        using var doc = JsonDocument.Parse(await ModelServerTools.ModelGenerate("m", "p"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Chat_returns_the_content()
    {
        Use(_ => Json("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"yo\"}}]}"));

        using var doc = JsonDocument.Parse(await ModelServerTools.ModelChat("m", [new ChatTurn("user", "hi")]));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("yo", root.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Clean_and_chat_returns_the_content_and_the_trimmed_count()
    {
        Use(_ => Json("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"done\"}}]}"));

        using var doc = JsonDocument.Parse(await ModelServerTools.CleanAndChat("m", "  go\u0007 ", null));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("done", root.GetProperty("content").GetString());
        Assert.Equal(0, root.GetProperty("historyTrimmed").GetInt32());
    }

    [Fact]
    public async Task Chat_invalid_role_is_ok_false()
    {
        Use(_ => Json("{}"));

        using var doc = JsonDocument.Parse(await ModelServerTools.ModelChat("m", [new ChatTurn("tool", "x")]));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }
}
