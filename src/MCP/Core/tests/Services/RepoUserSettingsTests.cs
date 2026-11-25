using AiDevHelpers.MCP.Core.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class RepoUserSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "repo-user-" + Guid.NewGuid().ToString("N"));

    public RepoUserSettingsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, ".aidevhelpers"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Solution()
    {
        var sln = Path.Combine(_root, "App.sln");
        File.WriteAllText(sln, "");
        return sln;
    }

    private void WriteSettings(string json) => File.WriteAllText(Path.Combine(_root, ".aidevhelpers", "settings.json"), json);

    [Fact]
    public void AllowAllIsRequestedOnlyByTheRepoFile()
    {
        WriteSettings("""{"allowAll":true}""");

        var result = RepoSettings.Resolve(new IdeToolsOptions(), Solution());

        Assert.True(result.AllowAllRequested);
    }

    [Fact]
    public void ElevationFilesAreResolvedWithoutASettingsFile()
    {
        File.WriteAllText(Path.Combine(_root, ".aidevhelpers", "elevation.json"), """{ "projects": [ { "path": "src/Drv/Drv.csproj" } ] }""");
        File.WriteAllText(Path.Combine(_root, ".aidevhelpers", "user.json"), """{ "elevation": { "mode": "never" } }""");

        var result = RepoSettings.Resolve(new IdeToolsOptions(), Solution());

        Assert.Equal(AiDevHelpers.Common.ElevationMode.Never, result.Elevation!.Mode);
        Assert.Equal("src/Drv/Drv.csproj", Assert.Single(result.Elevation.Projects).Path);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void NoRepoMeansNoElevationInfo()
    {
        Assert.Null(RepoSettings.Resolve(new IdeToolsOptions(), null).Elevation);
    }

    [Fact]
    public void AllowAllIsNotRequestedWhenTheRepoFileLacksIt()
    {
        WriteSettings("""{"IdeTools":{}}""");

        Assert.False(RepoSettings.Resolve(new IdeToolsOptions(), Solution()).AllowAllRequested);
    }

    [Fact]
    public void AllowAllInsideTheIdeToolsSectionDoesNotCount()
    {
        WriteSettings("""{"IdeTools":{"allowAll":true}}""");

        Assert.False(RepoSettings.Resolve(new IdeToolsOptions(), Solution()).AllowAllRequested);
    }

    [Fact]
    public void GlobalAllowAllIsIgnoredAndReported()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["allowAll"] = "true" })
            .Build();

        Assert.NotNull(RepoSettings.GlobalAllowAllWarning(config));
    }

    [Fact]
    public void NoGlobalAllowAll_NoWarning()
    {
        Assert.Null(RepoSettings.GlobalAllowAllWarning(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void SettingsHashIgnoresLineEndingsAndABom()
    {
        var a = Path.Combine(_root, "a.json");
        var b = Path.Combine(_root, "b.json");
        File.WriteAllBytes(a, [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. "{\r\n\"allowAll\":true\r\n}"u8.ToArray()]);
        File.WriteAllText(b, "{\n\"allowAll\":true\n}");

        Assert.Equal(RepoSettings.HashFile(a), RepoSettings.HashFile(b));
    }

    [Fact]
    public void SettingsHashChangesWithTheContent()
    {
        var a = Path.Combine(_root, "a.json");
        File.WriteAllText(a, """{"allowAll":true}""");
        var before = RepoSettings.HashFile(a);
        File.WriteAllText(a, """{"allowAll":false}""");

        Assert.NotEqual(before, RepoSettings.HashFile(a));
    }

    [Fact]
    public void EnsureGitIgnored_AddsTheUserFileOnce()
    {
        RepoSettings.EnsureUserFileGitIgnored(_root);
        RepoSettings.EnsureUserFileGitIgnored(_root);

        var lines = File.ReadAllLines(Path.Combine(_root, ".gitignore"));
        Assert.Single(lines, l => l == ".aidevhelpers/user.json");
    }

    [Fact]
    public void EnsureGitIgnored_KeepsExistingLines()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "bin/\nobj/");

        RepoSettings.EnsureUserFileGitIgnored(_root);

        var lines = File.ReadAllLines(Path.Combine(_root, ".gitignore"));
        Assert.Equal(["bin/", "obj/", ".aidevhelpers/user.json"], lines);
    }
}
