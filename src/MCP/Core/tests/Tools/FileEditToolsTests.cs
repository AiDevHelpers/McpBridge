using System.Text.Json;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class FileEditToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fe-tool-" + Guid.NewGuid().ToString("N"));

    public FileEditToolsTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "sample.txt"), "alpha beta alpha");
    }

    public void Dispose() => Directory.Delete(_root, true);

    private string Text => File.ReadAllText(Path.Combine(_root, "sample.txt"));

    [Fact]
    public async Task DryRun_reports_and_changes_nothing()
    {
        using var doc = JsonDocument.Parse(await FileEditTools.FileEdit(
            "apply", filePath: "sample.txt", oldText: "beta", newText: "gamma", dryRun: true, root: _root));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("dryRun").GetBoolean());
        Assert.Equal("alpha beta alpha", Text);
    }

    [Fact]
    public async Task Apply_then_rollback_round_trips()
    {
        using var edit = JsonDocument.Parse(await FileEditTools.FileEdit(
            "apply", filePath: "sample.txt", oldText: "beta", newText: "gamma", root: _root));
        Assert.True(edit.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("alpha gamma alpha", Text);
        var backup = edit.RootElement.GetProperty("backupPath").GetString();
        Assert.True(File.Exists(backup));

        using var back = JsonDocument.Parse(await FileEditTools.FileEdit(
            "rollback", backupPath: backup, targetFilePath: "sample.txt", root: _root));
        Assert.True(back.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("alpha beta alpha", Text);
    }

    [Fact]
    public async Task Path_outside_root_is_refused()
    {
        using var doc = JsonDocument.Parse(await FileEditTools.FileEdit(
            "apply", filePath: "../outside.txt", oldText: "a", newText: "b", root: _root));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("outside the workspace root", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Missing_parameter_is_reported()
    {
        using var doc = JsonDocument.Parse(await FileEditTools.FileEdit("apply", filePath: "sample.txt", root: _root));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("oldText is required for action 'apply'.", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Without_root_falls_back_to_the_repo_of_the_current_folder()
    {
        // The test host runs inside the repo, so a root is found; the edit then fails on a missing file, not on the root.
        using var doc = JsonDocument.Parse(await FileEditTools.FileEdit(
            "apply", filePath: "definitely-not-here-" + Guid.NewGuid().ToString("N") + ".txt", oldText: "a", newText: "b"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("File not found", doc.RootElement.GetProperty("error").GetString());
    }
}
