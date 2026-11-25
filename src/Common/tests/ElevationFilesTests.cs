using System.Text.Json.Nodes;
using Xunit;

namespace AiDevHelpers.Common.Tests;

public sealed class ElevationFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "elev-files-" + Guid.NewGuid().ToString("N"));

    public ElevationFilesTests() => Directory.CreateDirectory(Path.Combine(_root, ".aidevhelpers"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteElevation(string json) => File.WriteAllText(ElevationFiles.FilePath(_root), json);

    private void WriteUser(string json) => File.WriteAllText(RepoSettingsFile.UserFilePath(_root), json);

    [Fact]
    public void NoFiles_MeansListedWithNothingAndNoWarning()
    {
        var read = ElevationFiles.Read(_root);

        Assert.Equal(ElevationMode.Listed, read.Mode);
        Assert.Empty(read.Projects);
        Assert.Empty(read.Declined);
        Assert.Empty(read.Warnings);
    }

    [Fact]
    public void TheCommittedListAndTheUserChoices_AreRead()
    {
        WriteElevation("""{ "projects": [ { "path": "src/Drv/Drv.csproj", "reason": "manifest" }, "src/Other/Other.csproj" ] }""");
        WriteUser("""{ "elevation": { "mode": "always", "declined": ["src/Tool/Tool.csproj"] } }""");

        var read = ElevationFiles.Read(_root);

        Assert.Equal(ElevationMode.Always, read.Mode);
        Assert.Equal(["src/Drv/Drv.csproj", "src/Other/Other.csproj"], read.Projects.Select(p => p.Path).ToList());
        Assert.Equal("manifest", read.Projects[0].Reason);
        Assert.Equal(["src/Tool/Tool.csproj"], read.Declined);
    }

    [Fact]
    public void MalformedFiles_MeanNothingIsElevatedAndAWarningSaysWhy()
    {
        WriteElevation("{ not json");
        WriteUser("also not json");

        var read = ElevationFiles.Read(_root);

        Assert.Equal(ElevationMode.Listed, read.Mode);
        Assert.Empty(read.Projects);
        Assert.Equal(2, read.Warnings.Count);
    }

    [Theory]
    [InlineData("..\\outside\\x.csproj")]
    [InlineData("a/../../x.csproj")]
    [InlineData("C:\\Windows\\x.csproj")]
    [InlineData("/rooted/x.csproj")]
    [InlineData("")]
    public void ProjectPathsThatLeaveTheRepo_AreIgnoredWithAWarning(string path)
    {
        WriteElevation(new JsonObject { ["projects"] = new JsonArray(new JsonObject { ["path"] = path }, new JsonObject { ["path"] = "ok/ok.csproj" }) }.ToJsonString());

        var read = ElevationFiles.Read(_root);

        Assert.Equal(["ok/ok.csproj"], read.Projects.Select(p => p.Path).ToList());
        Assert.Single(read.Warnings);
    }

    [Fact]
    public void AnUnknownMode_IsIgnoredWithAWarning()
    {
        WriteUser("""{ "elevation": { "mode": "sometimes" } }""");

        var read = ElevationFiles.Read(_root);

        Assert.Equal(ElevationMode.Listed, read.Mode);
        Assert.Single(read.Warnings);
    }

    [Fact]
    public void AddProject_KeepsOtherKeysAndEntriesAndDoesNotDuplicate()
    {
        WriteElevation("""{ "note": "keep me", "projects": [ { "path": "a/a.csproj", "reason": "old", "extra": 1 } ] }""");

        ElevationFiles.AddProject(_root, "b\\b.csproj", "manifest");
        ElevationFiles.AddProject(_root, "a/a.csproj", "new reason");

        var root = JsonNode.Parse(File.ReadAllText(ElevationFiles.FilePath(_root)))!;
        Assert.Equal("keep me", root["note"]!.GetValue<string>());
        var projects = root["projects"]!.AsArray();
        Assert.Equal(2, projects.Count);
        Assert.Equal("new reason", projects[0]!["reason"]!.GetValue<string>());
        Assert.Equal(1, projects[0]!["extra"]!.GetValue<int>());
        Assert.Equal("b/b.csproj", projects[1]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void AddProject_RefusesAPathOutsideTheRepo_AndAMalformedFileIsNotOverwritten()
    {
        Assert.Throws<ArgumentException>(() => ElevationFiles.AddProject(_root, "../x.csproj", null));

        WriteElevation("{ broken");
        Assert.Throws<IOException>(() => ElevationFiles.AddProject(_root, "a/a.csproj", null));
        Assert.Equal("{ broken", File.ReadAllText(ElevationFiles.FilePath(_root)));
    }

    [Fact]
    public void SetModeAndDecline_KeepTheAllowAllPointerAndOtherKeys_AndGitIgnoreTheFile()
    {
        WriteUser("""{ "allowAll": { "projectId": "p1", "settingsHash": "h" }, "other": true }""");

        ElevationFiles.SetMode(_root, ElevationMode.Never);
        ElevationFiles.Decline(_root, "src\\Tool\\Tool.csproj");
        ElevationFiles.Decline(_root, "src/Tool/Tool.csproj");

        var root = JsonNode.Parse(File.ReadAllText(RepoSettingsFile.UserFilePath(_root)))!;
        Assert.Equal("p1", root["allowAll"]!["projectId"]!.GetValue<string>());
        Assert.True(root["other"]!.GetValue<bool>());
        Assert.Equal("never", root["elevation"]!["mode"]!.GetValue<string>());
        Assert.Single(root["elevation"]!["declined"]!.AsArray());
        Assert.Contains(".aidevhelpers/user.json", File.ReadAllLines(Path.Combine(_root, ".gitignore")));
        Assert.Equal(ElevationMode.Never, ElevationFiles.Read(_root).Mode);
    }

    [Fact]
    public void WritingElevationFiles_DoesNotChangeTheSettingsFileHash()
    {
        var settings = RepoSettingsFile.SettingsPath(_root);
        File.WriteAllText(settings, """{ "allowAll": true }""");
        var before = RepoSettingsFile.Hash(settings);

        ElevationFiles.AddProject(_root, "a/a.csproj", "x");
        ElevationFiles.SetMode(_root, ElevationMode.Always);

        Assert.Equal(before, RepoSettingsFile.Hash(settings));
    }
}
