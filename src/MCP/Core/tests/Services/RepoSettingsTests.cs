using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class RepoSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "repo-settings-" + Guid.NewGuid().ToString("N"));

    public RepoSettingsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Solution(string repoMarker = ".git", bool markerIsFile = false)
    {
        var sln = Path.Combine(_root, "src", "App.sln");
        Directory.CreateDirectory(Path.GetDirectoryName(sln)!);
        File.WriteAllText(sln, "");
        var marker = Path.Combine(_root, repoMarker);
        if (markerIsFile)
        {
            File.WriteAllText(marker, "gitdir: elsewhere");
        }
        else
        {
            Directory.CreateDirectory(marker);
        }

        return sln;
    }

    private void WriteSettings(string json)
    {
        var dir = Path.Combine(_root, ".aidevtools");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), json);
    }

    [Fact]
    public void RepoValueOverridesTheGlobalValue()
    {
        var sln = Solution();
        WriteSettings("""{"IdeTools":{"IdeTestRunMode":"DotnetTest"}}""");

        var result = RepoSettings.Resolve(new IdeToolsOptions { IdeTestRunMode = "TestExplorer" }, sln);

        Assert.Equal("DotnetTest", result.Options.IdeTestRunMode);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void KeysTheRepoDoesNotSetFallThroughToGlobal()
    {
        var sln = Solution();
        WriteSettings("""{"IdeTools":{}}""");

        var result = RepoSettings.Resolve(new IdeToolsOptions { IdeTestRunMode = "DotnetTest" }, sln);

        Assert.Equal("DotnetTest", result.Options.IdeTestRunMode);
    }

    [Fact]
    public void NoRepoFolder_UsesGlobal()
    {
        var sln = Solution();

        var result = RepoSettings.Resolve(new IdeToolsOptions { IdeTestRunMode = "DotnetTest" }, sln);

        Assert.Equal("DotnetTest", result.Options.IdeTestRunMode);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void NoSolution_UsesGlobal()
    {
        var result = RepoSettings.Resolve(new IdeToolsOptions { IdeTestRunMode = "DotnetTest" }, null);

        Assert.Equal("DotnetTest", result.Options.IdeTestRunMode);
    }

    [Fact]
    public void MalformedRepoFile_UsesGlobalAndWarns()
    {
        var sln = Solution();
        WriteSettings("{ not json");

        var result = RepoSettings.Resolve(new IdeToolsOptions { IdeTestRunMode = "TestExplorer" }, sln);

        Assert.Equal("TestExplorer", result.Options.IdeTestRunMode);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void UnknownKeysInTheRepoFileAreIgnoredAndReported()
    {
        var sln = Solution();
        WriteSettings("""{"IdeTools":{"IdeTestRunMode":"DotnetTest","Surprise":"x"}}""");

        var result = RepoSettings.Resolve(new IdeToolsOptions(), sln);

        Assert.Equal("DotnetTest", result.Options.IdeTestRunMode);
        Assert.Contains(result.Warnings, w => w.Contains("Surprise"));
    }

    [Fact]
    public void RepoRootIsFoundWhenDotGitIsAFile()
    {
        var sln = Solution(markerIsFile: true);
        WriteSettings("""{"IdeTools":{"IdeTestRunMode":"DotnetTest"}}""");

        var result = RepoSettings.Resolve(new IdeToolsOptions(), sln);

        Assert.Equal("DotnetTest", result.Options.IdeTestRunMode);
    }

    [Fact]
    public void ResolveDoesNotChangeTheGlobalOptions()
    {
        var sln = Solution();
        WriteSettings("""{"IdeTools":{"IdeTestRunMode":"DotnetTest"}}""");
        var global = new IdeToolsOptions { IdeTestRunMode = "TestExplorer" };

        RepoSettings.Resolve(global, sln);

        Assert.Equal("TestExplorer", global.IdeTestRunMode);
    }
}
