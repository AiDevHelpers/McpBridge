using Xunit;

namespace AiDevHelpers.Helpers.FileEditHelper.Tests;

public sealed class FileEditServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fe-svc-" + Guid.NewGuid().ToString("N"));
    private readonly FileEditService _svc = new(new FileEditEngine());

    public FileEditServiceTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "one two");
    }

    public void Dispose() => Directory.Delete(_root, true);

    private string Text => File.ReadAllText(Path.Combine(_root, "a.txt"));

    [Theory]
    [InlineData("apply")]
    [InlineData("  APPLY ")]
    [InlineData("Apply")]
    public async Task Action_is_trimmed_and_case_insensitive(string action)
    {
        var r = await _svc.ExecuteAsync(new FileEditRequest(action, FilePath: "a.txt", OldText: "one", NewText: "1"), _root);
        Assert.True(r.Ok);
        Assert.Equal("1 two", Text);
    }

    [Fact]
    public async Task Unknown_action_lists_the_valid_ones()
    {
        var r = await _svc.ExecuteAsync(new FileEditRequest("explode"), _root);
        Assert.False(r.Ok);
        Assert.Contains("apply", r.Error);
        Assert.Contains("rollback", r.Error);
    }

    [Theory]
    [InlineData("apply", null, "o", "n", "filePath")]
    [InlineData("apply", "a.txt", null, "n", "oldText")]
    [InlineData("apply", "a.txt", "o", null, "newText")]
    public async Task Apply_requires_its_parameters(string action, string? file, string? oldText, string? newText, string missing)
    {
        var r = await _svc.ExecuteAsync(new FileEditRequest(action, FilePath: file, OldText: oldText, NewText: newText), _root);
        Assert.False(r.Ok);
        Assert.Equal($"{missing} is required for action 'apply'.", r.Error);
    }

    [Fact]
    public async Task Insert_requires_anchor_and_content()
    {
        var a = await _svc.ExecuteAsync(new FileEditRequest("insert", FilePath: "a.txt", Content: "x"), _root);
        var c = await _svc.ExecuteAsync(new FileEditRequest("insert", FilePath: "a.txt", Anchor: "one"), _root);
        Assert.Equal("anchor is required for action 'insert'.", a.Error);
        Assert.Equal("content is required for action 'insert'.", c.Error);
    }

    [Theory]
    [InlineData(null, "one-X two")]
    [InlineData("after", "one-X two")]
    [InlineData("AFTER", "one-X two")]
    [InlineData("before", "X-one two")]
    public async Task Insert_position_defaults_to_after(string? position, string expectedShape)
    {
        var r = await _svc.ExecuteAsync(new FileEditRequest("insert", FilePath: "a.txt", Anchor: "one", Content: "X", Position: position), _root);
        Assert.True(r.Ok);
        Assert.Equal(expectedShape.StartsWith("X") ? "Xone two" : "oneX two", Text);
    }

    [Fact]
    public async Task Insert_bad_position_errors()
    {
        var r = await _svc.ExecuteAsync(new FileEditRequest("insert", FilePath: "a.txt", Anchor: "one", Content: "X", Position: "middle"), _root);
        Assert.False(r.Ok);
        Assert.Contains("before", r.Error);
    }

    [Fact]
    public async Task Delete_and_rename_work_through_the_service()
    {
        var d = await _svc.ExecuteAsync(new FileEditRequest("delete", FilePath: "a.txt", Pattern: " two"), _root);
        Assert.True(d.Ok);
        Assert.Equal("one", Text);
        var n = await _svc.ExecuteAsync(new FileEditRequest("rename", FilePath: "a.txt", OldName: "one", NewName: "uno"), _root);
        Assert.True(n.Ok);
        Assert.Equal("uno", Text);
    }

    [Fact]
    public async Task Rollback_requires_both_paths_and_restores()
    {
        var edit = await _svc.ExecuteAsync(new FileEditRequest("apply", FilePath: "a.txt", OldText: "one", NewText: "1"), _root);
        var missing = await _svc.ExecuteAsync(new FileEditRequest("rollback", BackupPath: edit.BackupPath), _root);
        Assert.Equal("targetFilePath is required for action 'rollback'.", missing.Error);
        var r = await _svc.ExecuteAsync(new FileEditRequest("rollback", BackupPath: edit.BackupPath, TargetFilePath: "a.txt"), _root);
        Assert.True(r.Ok);
        Assert.Equal("one two", Text);
    }

    [Fact]
    public async Task No_root_is_refused()
    {
        var r = await _svc.ExecuteAsync(new FileEditRequest("apply", FilePath: "a.txt", OldText: "one", NewText: "1"), null);
        Assert.False(r.Ok);
        Assert.Contains("No workspace root", r.Error);
    }
}
