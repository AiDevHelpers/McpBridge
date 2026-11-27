using System.Collections.Generic;
using System.Text.Json;
using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class ToolsetFileEditMapperTests
{
    private static Dictionary<string, JsonElement> Args(object values)
    {
        var element = JsonSerializer.SerializeToElement(values);
        var map = new Dictionary<string, JsonElement>();
        foreach (var property in element.EnumerateObject())
        {
            map[property.Name] = property.Value.Clone();
        }

        return map;
    }

    private static IReadOnlyList<IDictionary<string, object?>> Edits(FileEditMapping mapping) =>
        (IReadOnlyList<IDictionary<string, object?>>)mapping.Args["edits"]!;

    [Fact]
    public void ApplyMapsToOneReplaceInFileEdit()
    {
        var m = ToolsetFileEditMapper.Map(Args(new { action = "apply", filePath = "src/a.cs", oldText = "x", newText = "y" }));

        Assert.Null(m.Refusal);
        Assert.Equal("replaceInFile", m.Action);
        Assert.Equal("src/a.cs", m.Args["file"]);
        Assert.Equal("x", Edits(m)[0]["oldText"]);
        Assert.Equal("y", Edits(m)[0]["newText"]);
        Assert.Equal(true, m.Args["confirm"]);
    }

    [Fact]
    public void ApplyPassesTheOccurrenceThrough()
    {
        var m = ToolsetFileEditMapper.Map(Args(new { action = "apply", filePath = "a.cs", oldText = "x", newText = "y", occurrence = 2 }));

        Assert.Equal(2, Edits(m)[0]["occurrence"]);
    }

    [Fact]
    public void InsertAfterAnAnchorAppendsTheContent()
    {
        var m = ToolsetFileEditMapper.Map(Args(new { action = "insert", filePath = "a.cs", anchor = "X", content = "Y", position = "after" }));

        Assert.Equal("X", Edits(m)[0]["oldText"]);
        Assert.Equal("XY", Edits(m)[0]["newText"]);
    }

    [Fact]
    public void InsertBeforeAnAnchorPrependsTheContent()
    {
        var m = ToolsetFileEditMapper.Map(Args(new { action = "insert", filePath = "a.cs", anchor = "X", content = "Y", position = "before" }));

        Assert.Equal("YX", Edits(m)[0]["newText"]);
    }

    [Fact]
    public void DeleteLiteralReplacesWithNothing()
    {
        var m = ToolsetFileEditMapper.Map(Args(new { action = "delete", filePath = "a.cs", pattern = "gone" }));

        Assert.Equal("gone", Edits(m)[0]["oldText"]);
        Assert.Equal("", Edits(m)[0]["newText"]);
    }

    [Fact]
    public void DeleteRegexIsRefusedBecauseTheBridgeMatchesLiterally()
    {
        var m = ToolsetFileEditMapper.Map(Args(new { action = "delete", filePath = "a.cs", pattern = "g.*", isRegex = true }));

        Assert.NotNull(m.Refusal);
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("rollback")]
    public void RenameAndRollbackAreRefusedOnTheIdePaths(string action)
    {
        var m = ToolsetFileEditMapper.Map(Args(new { action, filePath = "a.cs", oldName = "A", newName = "B", backupPath = "b" }));

        Assert.NotNull(m.Refusal);
        Assert.Contains(action, m.Refusal);
    }

    [Fact]
    public void DryRunMapsToAPlanOnly()
    {
        var m = ToolsetFileEditMapper.Map(Args(new { action = "apply", filePath = "a.cs", oldText = "x", newText = "y", dryRun = true }));

        Assert.Equal(false, m.Args["confirm"]);
    }

    [Fact]
    public void UnknownActionIsRefused()
    {
        var m = ToolsetFileEditMapper.Map(Args(new { action = "explode", filePath = "a.cs" }));

        Assert.NotNull(m.Refusal);
    }
}
