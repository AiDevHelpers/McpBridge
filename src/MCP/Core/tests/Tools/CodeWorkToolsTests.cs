using System.Net;
using System.Text;
using System.Text.Json;
using AiDevHelpers.Helpers.ModelServerHelper;
using AiDevHelpers.MCP.Core.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

[Collection("CodeWorkTools")]
public sealed class CodeWorkToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cw-" + Guid.NewGuid().ToString("N"));

    public CodeWorkToolsTests() => Directory.CreateDirectory(_root);

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

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request));
        }
    }

    private static readonly string OnePlan =
        "[{\"id\":\"s1\",\"project\":\"App\",\"file\":\"src/Thing.cs\",\"template\":\"Class.cs\"}]";

    private Handler Router(string reply = "{\"response\":\"class Thing {}\"}")
    {
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(reply, Encoding.UTF8, "application/json"),
        });
        CodeWorkTools.GeneratorFactory = () => new ModelServerGenerator(
            new HttpClient(h), new Uri("http://localhost:11435"), (_, _) => Task.CompletedTask);
        return h;
    }

    private List<(string Action, string Json)> IdeCalls()
    {
        var calls = new List<(string, string)>();
        CodeWorkTools.IdeCall = (action, args, _) =>
        {
            calls.Add((action, JsonSerializer.Serialize(args)));
            return Task.FromResult("{\"ok\":true}");
        };
        return calls;
    }

    private static readonly string DifficultPlan =
        "[{\"id\":\"s1\",\"project\":\"App\",\"file\":\"src/Thing.cs\",\"template\":\"Class.cs\",\"difficult\":true}]";

    private static CodeWorkTools Tools(bool enabled = true, string draftType = "code", string difficultType = "hard")
    {
        var cw = new CodeWorkOptions { Enabled = enabled, DraftModelType = draftType, DifficultModelType = difficultType };
        var ms = new ModelServerOptions();
        ms.ModelTypes["code"] = new ModelTypeSetting { Target = "router", Model = "qwen-coder" };
        ms.ModelTypes["big"] = new ModelTypeSetting { Target = "router", Model = "big-model" };
        ms.ModelTypes["hard"] = new ModelTypeSetting { Target = "chat", Model = "sonnet" };
        return new CodeWorkTools(Options.Create(cw), Options.Create(ms));
    }

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);

    [Fact]
    public async Task A_disabled_server_refuses_every_action()
    {
        var reply = await Tools(enabled: false).CodeWorkStart(_root, OnePlan, "build it");

        Assert.Contains("AUTO_DISABLED", reply);
    }

    [Fact]
    public async Task Start_without_confirm_saves_the_plan_and_creates_nothing()
    {
        var calls = IdeCalls();

        using var doc = Parse(await Tools().CodeWorkStart(_root, OnePlan, "build it", sessionId: "s-plan"));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(calls);
        Assert.True(File.Exists(Path.Combine(_root, ".aidevhelpers", "code-work", "s-plan.json")));
    }

    [Fact]
    public async Task Start_with_confirm_creates_each_step_from_its_template()
    {
        var calls = IdeCalls();

        using var doc = Parse(await Tools().CodeWorkStart(_root, OnePlan, "build it", sessionId: "s-make", confirm: true));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var call = Assert.Single(calls);
        Assert.Equal("addProjectItem", call.Action);
        Assert.Contains("\"mode\":\"fromNew\"", call.Json);
        Assert.Contains("\"confirm\":true", call.Json);
        Assert.Contains("Class.cs", call.Json);
    }

    [Fact]
    public async Task Start_refuses_a_file_outside_the_root()
    {
        IdeCalls();
        var plan = "[{\"id\":\"s1\",\"project\":\"App\",\"file\":\"../escape.cs\",\"template\":\"Class.cs\"}]";

        using var doc = Parse(await Tools().CodeWorkStart(_root, plan, "p", sessionId: "s-out"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Start_refuses_duplicate_step_ids()
    {
        var plan = "[{\"id\":\"s1\",\"project\":\"App\",\"file\":\"a.cs\",\"template\":\"Class.cs\"},{\"id\":\"s1\",\"project\":\"App\",\"file\":\"b.cs\",\"template\":\"Class.cs\"}]";

        using var doc = Parse(await Tools().CodeWorkStart(_root, plan, "p", sessionId: "s-dup"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task A_session_id_with_path_characters_is_refused()
    {
        using var doc = Parse(await Tools().CodeWorkStart(_root, OnePlan, "p", sessionId: "../x"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Continue_drafts_with_the_router_type_and_returns_a_token_without_writing()
    {
        Router();
        await Tools().CodeWorkStart(_root, OnePlan, "build it", sessionId: "s-draft");

        using var doc = Parse(await Tools().CodeWorkContinue(_root, "s-draft", "s1"));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("class Thing {}", root.GetProperty("content").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("token").GetString()));
        Assert.False(File.Exists(Path.Combine(_root, "src", "Thing.cs")));
    }

    [Fact]
    public async Task Continue_with_a_chat_draft_type_hands_off_and_calls_no_router()
    {
        var h = Router();
        await Tools().CodeWorkStart(_root, OnePlan, "build it", sessionId: "s-chat");

        using var doc = Parse(await Tools(draftType: "hard").CodeWorkContinue(_root, "s-chat", "s1"));

        Assert.True(doc.RootElement.GetProperty("handoff").GetBoolean());
        Assert.Equal("sonnet", doc.RootElement.GetProperty("model").GetString());
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task A_difficult_step_drafts_with_the_difficult_router_model()
    {
        Router();
        await Tools(difficultType: "big").CodeWorkStart(_root, DifficultPlan, "build it", sessionId: "s-big");

        using var doc = Parse(await Tools(difficultType: "big").CodeWorkContinue(_root, "s-big", "s1"));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("big-model", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task A_step_that_is_not_difficult_uses_the_draft_model()
    {
        Router();
        await Tools(difficultType: "big").CodeWorkStart(_root, OnePlan, "build it", sessionId: "s-plain");

        using var doc = Parse(await Tools(difficultType: "big").CodeWorkContinue(_root, "s-plain", "s1"));

        Assert.Equal("qwen-coder", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task A_difficult_step_on_a_chat_type_hands_off_and_confirm_takes_the_content()
    {
        var h = Router();
        await Tools().CodeWorkStart(_root, DifficultPlan, "build it", sessionId: "s-handoff");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Thing.cs"), "old\n");

        using var drafted = Parse(await Tools().CodeWorkContinue(_root, "s-handoff", "s1"));

        Assert.True(drafted.RootElement.GetProperty("handoff").GetBoolean());
        Assert.Equal("sonnet", drafted.RootElement.GetProperty("model").GetString());
        Assert.Empty(h.Paths);
        var token = drafted.RootElement.GetProperty("token").GetString()!;

        using var doc = Parse(await Tools().CodeWorkConfirm(_root, "s-handoff", "s1", token, content: "class Thing {}"));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("class Thing {}", File.ReadAllText(Path.Combine(_root, "src", "Thing.cs")));
    }

    [Fact]
    public async Task Confirm_of_a_handoff_draft_without_content_is_refused()
    {
        await Tools().CodeWorkStart(_root, DifficultPlan, "build it", sessionId: "s-nocontent");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Thing.cs"), "old\n");
        using var drafted = Parse(await Tools().CodeWorkContinue(_root, "s-nocontent", "s1"));
        var token = drafted.RootElement.GetProperty("token").GetString()!;

        using var doc = Parse(await Tools().CodeWorkConfirm(_root, "s-nocontent", "s1", token));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("old\n", File.ReadAllText(Path.Combine(_root, "src", "Thing.cs")));
    }

    [Fact]
    public async Task Continue_an_unknown_step_is_refused()
    {
        Router();
        await Tools().CodeWorkStart(_root, OnePlan, "build it", sessionId: "s-unk");

        using var doc = Parse(await Tools().CodeWorkContinue(_root, "s-unk", "nope"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Confirm_with_a_wrong_token_writes_nothing()
    {
        Router();
        await Tools().CodeWorkStart(_root, OnePlan, "build it", sessionId: "s-tok");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Thing.cs"), "old\n");
        await Tools().CodeWorkContinue(_root, "s-tok", "s1");

        using var doc = Parse(await Tools().CodeWorkConfirm(_root, "s-tok", "s1", "wrong"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("old\n", File.ReadAllText(Path.Combine(_root, "src", "Thing.cs")));
    }

    [Fact]
    public async Task Confirm_writes_the_checked_draft_through_file_edit()
    {
        Router();
        await Tools().CodeWorkStart(_root, OnePlan, "build it", sessionId: "s-write");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Thing.cs"), "old\n");
        using var drafted = Parse(await Tools().CodeWorkContinue(_root, "s-write", "s1"));
        var token = drafted.RootElement.GetProperty("token").GetString()!;

        using var doc = Parse(await Tools().CodeWorkConfirm(_root, "s-write", "s1", token));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("class Thing {}", File.ReadAllText(Path.Combine(_root, "src", "Thing.cs")));
    }
}
