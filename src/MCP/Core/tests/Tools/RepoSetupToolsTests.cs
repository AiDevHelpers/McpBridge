using System.Text.Json;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class RepoSetupToolsTests : IDisposable
{
    private const string ScanJson = """
        {"root":"x","findings":[],"pendingProjects":["src/Drv/Drv.csproj"],
         "elevation":{"mode":"listed","projects":[{"path":"a/a.csproj","reason":"r"}],"declined":["b/b.csproj"],"warnings":["w"]}}
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

        public Task<ElevRunResult> ScanAsync(string repoRoot, CancellationToken ct)
        {
            Roots.Add(repoRoot);
            return Task.FromResult(new ElevRunResult(exit, output));
        }
    }

    [Fact]
    public async Task Scan_ReturnsTheScanAndTellsTheAiToAskTheUser_AndScansTheRepoRoot()
    {
        var scanner = new FakeScanner(0, ScanJson);

        var text = await new RepoSetupTools(scanner).RepoSetup(Path.Combine(_root, "sub"), "scan");

        Assert.Equal([_root], scanner.Roots);
        Assert.Contains("pendingProjects", text);
        Assert.Contains("elev repo-setup " + _root, text);
    }

    [Fact]
    public async Task Status_ReturnsTheModeTheListAndWhatIsPending()
    {
        var text = await new RepoSetupTools(new FakeScanner(0, ScanJson)).RepoSetup(_root);

        using var doc = JsonDocument.Parse(text);
        Assert.Equal("listed", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("a/a.csproj", doc.RootElement.GetProperty("projects")[0].GetProperty("path").GetString());
        Assert.Equal("b/b.csproj", doc.RootElement.GetProperty("declined")[0].GetString());
        Assert.Equal("src/Drv/Drv.csproj", doc.RootElement.GetProperty("pendingProjects")[0].GetString());
        Assert.Equal("w", doc.RootElement.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public async Task AFolderOutsideGit_AndAnUnknownAction_AreErrors()
    {
        var tool = new RepoSetupTools(new FakeScanner(0, ScanJson));

        Assert.StartsWith("Error:", await tool.RepoSetup(Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid()), "scan"));
        Assert.StartsWith("Error:", await tool.RepoSetup(_root, "apply"));
    }

    [Fact]
    public async Task AFailedScan_IsReportedAsAnError()
    {
        var text = await new RepoSetupTools(new FakeScanner(1, "boom")).RepoSetup(_root, "scan");

        Assert.StartsWith("Error:", text);
        Assert.Contains("boom", text);
    }

    [Fact]
    public async Task TheTool_NeverCreatesTheSetupFolder()
    {
        await new RepoSetupTools(new FakeScanner(0, ScanJson)).RepoSetup(_root, "scan");

        Assert.False(Directory.Exists(Path.Combine(_root, ".aidevtools")));
    }
}
