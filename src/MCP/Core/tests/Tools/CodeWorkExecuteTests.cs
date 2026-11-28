using System.Net;
using System.Text;
using System.Text.Json;
using AiDevHelpers.Helpers.ModelServerHelper;
using AiDevHelpers.MCP.Core.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

[Collection("CodeWorkTools")]
public sealed class CodeWorkExecuteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cwx-" + Guid.NewGuid().ToString("N"));

    public CodeWorkExecuteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        CodeWorkTools.GeneratorFactory = null;
        CodeWorkTools.IdeCall = null;
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // a locked file in the temp folder is not a test failure
        }
    }

    private const string OnePlan = "[{\"id\":\"s1\",\"project\":\"App\",\"file\":\"src/Thing.cs\",\"template\":\"Class.cs\"}]";

    private List<string> IdeCalls()
    {
        var actions = new List<string>();
        CodeWorkTools.IdeCall = (action, _, _) =>
        {
            actions.Add(action);
            return Task.FromResult("{\"ok\":true}");
        };
        return actions;
    }

    private static void Router()
    {
        var h = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"response\":\"class Thing {}\"}", Encoding.UTF8, "application/json"),
        }));
        CodeWorkTools.GeneratorFactory = () => new ModelServerGenerator(h, new Uri("http://localhost:11435"), (_, _) => Task.CompletedTask);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private static CodeWorkTools Tools(bool enabled = true)
    {
        var cw = new CodeWorkOptions { Enabled = enabled, DraftModelType = "code" };
        var ms = new ModelServerOptions();
        ms.ModelTypes["code"] = new ModelTypeSetting { Target = "router", Model = "qwen-coder" };
        return new CodeWorkTools(Options.Create(cw), Options.Create(ms));
    }

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);

    [Fact]
    public async Task The_first_execute_creates_the_file_from_its_template()
    {
        var actions = IdeCalls();
        await Tools().CodeWorkStart(_root, OnePlan, "build it", sessionId: "x-create");

        using var doc = Parse(await Tools().CodeWorkExecute(_root, "x-create", "s1"));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("created", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(["addProjectItem"], actions);
    }

    [Fact]
    public async Task The_second_execute_drafts_the_content()
    {
        IdeCalls();
        Router();
        await Tools().CodeWorkStart(_root, OnePlan, "build it", sessionId: "x-draft");
        await Tools().CodeWorkExecute(_root, "x-draft", "s1");

        using var doc = Parse(await Tools().CodeWorkExecute(_root, "x-draft", "s1"));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("drafted", doc.RootElement.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("token").GetString()));
    }

    [Fact]
    public async Task A_third_execute_asks_for_confirm_and_changes_nothing()
    {
        var actions = IdeCalls();
        Router();
        await Tools().CodeWorkStart(_root, OnePlan, "build it", sessionId: "x-third");
        await Tools().CodeWorkExecute(_root, "x-third", "s1");
        await Tools().CodeWorkExecute(_root, "x-third", "s1");
        var before = actions.Count;

        using var doc = Parse(await Tools().CodeWorkExecute(_root, "x-third", "s1"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(before, actions.Count);
        Assert.Contains("confirm", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unknown_step_is_refused()
    {
        IdeCalls();
        await Tools().CodeWorkStart(_root, OnePlan, "build it", sessionId: "x-unk");

        using var doc = Parse(await Tools().CodeWorkExecute(_root, "x-unk", "nope"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task A_disabled_server_refuses_execute()
    {
        var actions = IdeCalls();
        await Tools().CodeWorkStart(_root, OnePlan, "build it", sessionId: "x-off");

        using var doc = Parse(await Tools(enabled: false).CodeWorkExecute(_root, "x-off", "s1"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(actions);
    }

    [Fact]
    public async Task Start_with_confirm_marks_the_steps_created()
    {
        IdeCalls();
        await Tools().CodeWorkStart(_root, OnePlan, "build it", sessionId: "x-created", confirm: true);

        using var doc = Parse(await Tools().CodeWorkExecute(_root, "x-created", "s1"));

        Assert.Equal("drafted", doc.RootElement.GetProperty("status").GetString());
    }
}
