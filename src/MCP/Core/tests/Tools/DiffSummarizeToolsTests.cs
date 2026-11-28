using System.Net;
using System.Text;
using System.Text.Json;
using AiDevHelpers.Helpers.ModelServerHelper;
using AiDevHelpers.Helpers.SourceControlHelper;
using AiDevHelpers.MCP.Core.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

[Collection("DiffSummarizeTools")]
public sealed class DiffSummarizeToolsTests : IDisposable
{
    public void Dispose()
    {
        DiffSummarizeTools.GeneratorFactory = null;
        DiffSummarizeTools.ServiceFactory = null;
    }

    private sealed class FakeGit(GitResult numstat, GitResult raw) : IGitRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            Calls.Add(arguments);
            return Task.FromResult(arguments.Contains("--numstat") ? numstat : raw);
        }
    }

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

    private const string Numstat = "3\t1\tsrc/a.cs\n";
    private const string Raw = "diff --git a/src/a.cs b/src/a.cs\n+added\n";

    private static readonly (string Name, string Target, string Model)[] Settings =
    [
        ("code", "router", "qwen-coder"),
        ("hard", "chat", "sonnet"),
    ];

    private static Handler Router(string reply = "{\"response\":\"It adds a line.\"}")
    {
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(reply, Encoding.UTF8, "application/json"),
        });
        DiffSummarizeTools.GeneratorFactory = () => new ModelServerGenerator(
            new HttpClient(h), new Uri("http://localhost:11435"), (_, _) => Task.CompletedTask);
        return h;
    }

    private static void Git(string numstat = Numstat, string raw = Raw, bool fail = false)
    {
        var git = fail
            ? new FakeGit(new GitResult(false, 128, "fatal: not a git repository"), new GitResult(false, 128, "fatal: not a git repository"))
            : new FakeGit(new GitResult(true, 0, numstat), new GitResult(true, 0, raw));
        DiffSummarizeTools.ServiceFactory = () => new SourceControlService(git);
    }

    private static DiffSummarizeTools Tools()
    {
        var options = new ModelServerOptions();
        foreach (var (name, target, model) in Settings)
            options.ModelTypes[name] = new ModelTypeSetting { Target = target, Model = model };
        return new DiffSummarizeTools(Options.Create(options));
    }

    [Fact]
    public async Task Without_a_model_it_returns_stats_only_with_a_note()
    {
        var h = Router();
        Git();

        using var doc = JsonDocument.Parse(await Tools().DiffSummarize("repo"));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(1, root.GetProperty("files").GetArrayLength());
        Assert.Equal("src/a.cs", root.GetProperty("files")[0].GetProperty("path").GetString());
        Assert.Equal(3, root.GetProperty("added").GetInt32());
        Assert.Equal(1, root.GetProperty("deleted").GetInt32());
        Assert.Equal(Raw.Length, root.GetProperty("rawChars").GetInt32());
        Assert.False(root.GetProperty("diffTruncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("summary").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("note").GetString()));
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task A_router_type_summarizes_the_diff_on_the_router()
    {
        var h = Router();
        Git();

        using var doc = JsonDocument.Parse(await Tools().DiffSummarize("repo", modelType: "code"));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("It adds a line.", root.GetProperty("summary").GetString());
        Assert.Equal("qwen-coder", root.GetProperty("model").GetString());
        Assert.Equal(["/api/generate"], h.Paths);
        using var body = JsonDocument.Parse(h.Bodies[0]);
        Assert.Contains("+added", body.RootElement.GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task A_chat_type_hands_the_diff_back_and_calls_no_router()
    {
        var h = Router();
        Git();

        using var doc = JsonDocument.Parse(await Tools().DiffSummarize("repo", modelType: "hard"));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("handoff").GetBoolean());
        Assert.Equal("sonnet", root.GetProperty("model").GetString());
        Assert.Contains("+added", root.GetProperty("prompt").GetString());
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task A_literal_model_summarizes_on_the_router()
    {
        var h = Router();
        Git();

        using var doc = JsonDocument.Parse(await Tools().DiffSummarize("repo", model: "m"));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("m", doc.RootElement.GetProperty("model").GetString());
        Assert.Single(h.Paths);
    }

    [Fact]
    public async Task A_large_diff_is_cut_and_flagged()
    {
        var h = Router();
        Git(raw: new string('+', 16000) + "\n");

        using var doc = JsonDocument.Parse(await Tools().DiffSummarize("repo", modelType: "code"));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("diffTruncated").GetBoolean());
        Assert.Contains("[diff truncated]", h.Bodies[0]);
    }

    [Fact]
    public async Task Both_modelType_and_model_are_refused()
    {
        var h = Router();
        Git();

        using var doc = JsonDocument.Parse(await Tools().DiffSummarize("repo", modelType: "code", model: "m"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task An_unknown_type_is_refused_before_git_runs()
    {
        var h = Router();
        Git();

        using var doc = JsonDocument.Parse(await Tools().DiffSummarize("repo", modelType: "translate"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task A_path_outside_the_repo_is_refused_and_reaches_no_model()
    {
        var h = Router();
        Git();

        using var doc = JsonDocument.Parse(await Tools().DiffSummarize("repo", path: "../secret.txt", modelType: "code"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task A_git_failure_is_ok_false_with_the_output()
    {
        Router();
        Git(fail: true);

        using var doc = JsonDocument.Parse(await Tools().DiffSummarize("repo"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("not a git repository", doc.RootElement.GetProperty("error").GetString());
    }
}
