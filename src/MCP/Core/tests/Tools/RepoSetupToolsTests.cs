using System.Text.Json;
using AiDevHelpers.Common;
using AiDevHelpers.Helpers.SourceControlHelper;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class RepoSetupToolsTests : IDisposable
{
    private const string ScanJson = """
        {"root":"x","findings":[],"pendingProjects":["src/Drv/Drv.csproj"],
         "elevation":{"mode":"listed","projects":[{"path":"a/a.csproj","reason":"r"}],"declined":["b/b.csproj"],"warnings":["w"]},
         "rules":{"defaultBranch":"main","candidateProtectedBranches":["main","dev"],"candidateRequiredChecks":["build"]},
         "recordedRules":{"protectedBranches":["release/*"]},
         "pendingProtectedBranches":["main","dev"],"pendingRequiredChecks":["build"]}
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "repo-setup-tool-" + Guid.NewGuid().ToString("N"));

    public RepoSetupToolsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FakeScanner(int exit, string output) : IElevScanner
    {
        public List<string> Roots { get; } = [];

        public Task<ElevRunResult> ApplyAsync(string repoRoot, string decisionsJson, CancellationToken ct) => Task.FromResult(new ElevRunResult(0, "applied"));

        public Task<ElevRunResult> ScanAsync(string repoRoot, CancellationToken ct)
        {
            Roots.Add(repoRoot);
            return Task.FromResult(new ElevRunResult(exit, output));
        }
    }

    private sealed class FakeGh(bool works) : IGhRunner
    {
        public List<string> Calls { get; } = [];

        public Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            Calls.Add(string.Join(' ', arguments));
            if (!works)
                return Task.FromResult(new GitResult(false, -1, "gh is not installed"));

            return Task.FromResult(arguments[1].Contains("/protection")
                ? new GitResult(true, 0, """{"url":"u","required_status_checks":{"contexts":["build"]}}""")
                : new GitResult(true, 0, "[]"));
        }
    }

    private RepoSetupTools Tool(FakeScanner scanner, FakeGh? gh = null) => new(scanner, new GitHubService(gh ?? new FakeGh(works: false)));

    [Fact]
    public async Task Scan_ReturnsTheScanAndTheLivePolicy_AndTellsTheAiToAskTheUser()
    {
        var scanner = new FakeScanner(0, ScanJson);
        var gh = new FakeGh(works: true);

        var text = await Tool(scanner, gh).RepoSetup(Path.Combine(_root, "sub"), "scan");

        Assert.Equal([_root], scanner.Roots);
        Assert.Contains("pendingProjects", text);
        Assert.Contains("\"live\"", text);
        Assert.Contains("elev repo-setup " + _root, text);
        // The branches named by the repo's own files are the ones asked about, without duplicates.
        Assert.Contains(gh.Calls, c => c.Contains("branches/main/protection"));
        Assert.Contains(gh.Calls, c => c.Contains("branches/dev/protection"));
        Assert.Contains(gh.Calls, c => c.Contains("branches/main/protection") && gh.Calls.Count(x => x == c) == 1);
    }

    [Fact]
    public async Task Status_ReturnsTheModeTheListThePendingItemsAndTheLiveState()
    {
        var text = await Tool(new FakeScanner(0, ScanJson)).RepoSetup(_root);

        using var doc = JsonDocument.Parse(text);
        Assert.Equal("listed", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("a/a.csproj", doc.RootElement.GetProperty("projects")[0].GetProperty("path").GetString());
        Assert.Equal("b/b.csproj", doc.RootElement.GetProperty("declined")[0].GetString());
        Assert.Equal("src/Drv/Drv.csproj", doc.RootElement.GetProperty("pendingProjects")[0].GetString());
        Assert.Equal("main", doc.RootElement.GetProperty("pendingProtectedBranches")[0].GetString());
        Assert.Equal("w", doc.RootElement.GetProperty("warnings")[0].GetString());
        Assert.False(doc.RootElement.GetProperty("live").GetProperty("checked").GetBoolean());
        Assert.Contains("not installed", doc.RootElement.GetProperty("live").GetProperty("error").GetString());
    }

    [Fact]
    public async Task AFolderOutsideGit_AndAnUnknownAction_AreErrors()
    {
        var tool = Tool(new FakeScanner(0, ScanJson));

        Assert.StartsWith("Error:", await tool.RepoSetup(Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid()), "scan"));
        Assert.StartsWith("Error:", await tool.RepoSetup(_root, "apply"));
    }

    [Fact]
    public async Task AFailedScan_IsReportedAsAnError()
    {
        var text = await Tool(new FakeScanner(1, "boom")).RepoSetup(_root, "scan");

        Assert.StartsWith("Error:", text);
        Assert.Contains("boom", text);
    }

    [Fact]
    public async Task TheTool_NeverCreatesTheSetupFolder_EvenWhenGitHubAnswers()
    {
        await Tool(new FakeScanner(0, ScanJson), new FakeGh(works: true)).RepoSetup(_root, "scan");

        Assert.False(Directory.Exists(Path.Combine(_root, ".aidevhelpers")));
    }

    [Fact]
    public async Task InARepoThatIsSetUp_TheLiveAnswerIsCachedForTheGitTools()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".aidevhelpers"));

        await Tool(new FakeScanner(0, ScanJson), new FakeGh(works: true)).RepoSetup(_root, "status");

        var live = RepoRulesFile.ReadLive(_root);
        Assert.NotNull(live);
        Assert.Contains("main", live!.ProtectedBranches);
        Assert.Contains("build", live.RequiredChecks);
    }
}
