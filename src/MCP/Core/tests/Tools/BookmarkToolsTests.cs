using System.Text.Json;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

/// <summary>Refusal paths only: no bridge call succeeds, because no VS bookmark route exists yet.</summary>
public sealed class BookmarkToolsTests
{
    [Fact]
    public async Task BookmarkSet_RelativePath_IsRefused()
    {
        using var doc = JsonDocument.Parse(await BookmarkTools.BookmarkSet("Program.cs", 3));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("full path", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task BookmarkGet_EmptyId_IsRefused()
    {
        using var doc = JsonDocument.Parse(await BookmarkTools.BookmarkGet(" "));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task BookmarkRemove_EmptyId_IsRefused()
    {
        using var doc = JsonDocument.Parse(await BookmarkTools.BookmarkRemove(""));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }
}
