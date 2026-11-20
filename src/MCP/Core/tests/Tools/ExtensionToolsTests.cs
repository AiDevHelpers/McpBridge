using System.Text.Json;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

/// <summary>Only argument handling and refusal paths: no command runs and no network call is made.</summary>
public sealed class ExtensionToolsTests
{
    [Fact]
    public async Task ExtensionList_UnknownIde_ReportsError()
    {
        using var doc = JsonDocument.Parse(await ExtensionTools.ExtensionList("Emacs"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ExtensionAddConfirm_UnknownToken_ReportsError()
    {
        using var doc = JsonDocument.Parse(await ExtensionTools.ExtensionAddConfirm("no-such-token"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ExtensionInfo_UnknownIde_ReportsError()
    {
        using var doc = JsonDocument.Parse(await ExtensionTools.ExtensionInfo("Emacs", "pub.tool"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }
}
