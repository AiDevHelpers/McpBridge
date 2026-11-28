using System.Net;
using System.Text;
using System.Text.Json;
using AiDevHelpers.Helpers.ModelServerHelper;
using AiDevHelpers.MCP.Core.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

[Collection("ModelServerTools")]
public sealed class ModelGenerateToolsTests : IDisposable
{
    public void Dispose() => ModelServerTools.GeneratorFactory = null;

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Handler Use(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var h = new Handler(respond);
        ModelServerTools.GeneratorFactory = () => new ModelServerGenerator(
            new HttpClient(h), new Uri("http://localhost:11435"), (_, _) => Task.CompletedTask);
        return h;
    }

    private static ModelServerTools Tools(params (string Name, string Target, string Model)[] types)
    {
        var options = new ModelServerOptions();
        foreach (var (name, target, model) in types)
            options.ModelTypes[name] = new ModelTypeSetting { Target = target, Model = model };
        return new ModelServerTools(Options.Create(options));
    }

    private static readonly (string Name, string Target, string Model)[] Settings =
    [
        ("code", "router", "qwen-coder"),
        ("hard", "chat", "sonnet"),
    ];

    [Fact]
    public async Task Generate_with_a_router_type_runs_on_the_router_with_its_model()
    {
        var h = Use(_ => Json("{\"response\":\"hi there\"}"));

        using var doc = JsonDocument.Parse(await Tools(Settings).ModelGenerate("say hi", modelType: "code"));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("hi there", root.GetProperty("response").GetString());
        Assert.Equal("qwen-coder", root.GetProperty("model").GetString());
        Assert.Contains("qwen-coder", h.Bodies[0]);
    }

    [Fact]
    public async Task Generate_with_a_chat_type_hands_off_and_sends_nothing_to_the_router()
    {
        var h = Use(_ => Json("{\"response\":\"x\"}"));

        using var doc = JsonDocument.Parse(await Tools(Settings).ModelGenerate("review this", modelType: "hard"));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.True(root.GetProperty("handoff").GetBoolean());
        Assert.Equal("sonnet", root.GetProperty("model").GetString());
        Assert.Contains("sonnet", root.GetProperty("prompt").GetString());
        Assert.Contains("review this", root.GetProperty("prompt").GetString());
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task Generate_with_a_literal_model_runs_on_the_router()
    {
        var h = Use(_ => Json("{\"response\":\"ok\"}"));

        using var doc = JsonDocument.Parse(await Tools().ModelGenerate("p", model: "m"));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(["/api/generate"], h.Paths);
    }

    [Fact]
    public async Task Generate_with_both_modelType_and_model_is_refused()
    {
        var h = Use(_ => Json("{\"response\":\"x\"}"));

        using var doc = JsonDocument.Parse(await Tools(Settings).ModelGenerate("p", modelType: "code", model: "m"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task Generate_with_neither_is_refused()
    {
        var h = Use(_ => Json("{\"response\":\"x\"}"));

        using var doc = JsonDocument.Parse(await Tools(Settings).ModelGenerate("p"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task Generate_with_an_unknown_type_is_refused()
    {
        var h = Use(_ => Json("{\"response\":\"x\"}"));

        using var doc = JsonDocument.Parse(await Tools(Settings).ModelGenerate("p", modelType: "translate"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task Generate_unreachable_router_is_ok_false()
    {
        Use(_ => throw new HttpRequestException("refused"));

        using var doc = JsonDocument.Parse(await Tools(Settings).ModelGenerate("p", modelType: "code"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Chat_with_a_router_type_returns_the_content()
    {
        Use(_ => Json("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"yo\"}}]}"));

        using var doc = JsonDocument.Parse(await Tools(Settings).ModelChat([new ChatTurn("user", "hi")], modelType: "code"));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("yo", root.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Chat_with_a_chat_type_hands_the_messages_back()
    {
        var h = Use(_ => Json("{}"));

        using var doc = JsonDocument.Parse(await Tools(Settings).ModelChat([new ChatTurn("user", "hi")], modelType: "hard"));

        Assert.True(doc.RootElement.GetProperty("handoff").GetBoolean());
        Assert.Equal("sonnet", doc.RootElement.GetProperty("model").GetString());
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task Chat_invalid_role_is_ok_false()
    {
        Use(_ => Json("{}"));

        using var doc = JsonDocument.Parse(await Tools().ModelChat([new ChatTurn("tool", "x")], model: "m"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Clean_and_chat_with_a_router_type_returns_the_content_and_the_trimmed_count()
    {
        Use(_ => Json("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"done\"}}]}"));

        using var doc = JsonDocument.Parse(await Tools(Settings).CleanAndChat("  go\u0007 ", modelType: "code"));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("done", root.GetProperty("content").GetString());
        Assert.Equal(0, root.GetProperty("historyTrimmed").GetInt32());
    }

    [Fact]
    public async Task Clean_and_chat_with_a_chat_type_hands_off_the_cleaned_prompt()
    {
        var h = Use(_ => Json("{}"));

        using var doc = JsonDocument.Parse(await Tools(Settings).CleanAndChat("  big  task ", modelType: "hard"));

        Assert.True(doc.RootElement.GetProperty("handoff").GetBoolean());
        Assert.Contains("big  task", doc.RootElement.GetProperty("prompt").GetString());
        Assert.Empty(h.Paths);
    }

    [Fact]
    public void Model_route_returns_the_target_and_model_for_a_configured_type()
    {
        using var doc = JsonDocument.Parse(Tools(Settings).ModelRoute("hard"));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("chat", doc.RootElement.GetProperty("target").GetString());
        Assert.Equal("sonnet", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void Model_route_unknown_type_is_ok_false()
    {
        using var doc = JsonDocument.Parse(Tools(Settings).ModelRoute("translate"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }
}
