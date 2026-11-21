using System.Text.Json;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class ConfigToolsTests
{
    [Fact]
    public void ConfigResolve_UnknownKind_ReportsError()
    {
        using var doc = JsonDocument.Parse(ConfigTools.ConfigResolve("NotAKind"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Unknown config kind", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void ConfigResolve_SolutionSettingsWithoutSolution_ReportsError()
    {
        using var doc = JsonDocument.Parse(ConfigTools.ConfigResolve("SolutionSettings"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.NotNull(doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void ConfigRead_PathOutsideAllowedRoots_IsRefused()
    {
        using var doc = JsonDocument.Parse(ConfigTools.ConfigRead(@"C:\__config_no_such__\secret.json"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("outside", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void ConfigRead_TraversalOutOfUserRoot_IsRefused()
    {
        using var doc = JsonDocument.Parse(ConfigTools.ConfigRead(@"C:\Users\__nobody__\..\..\Windows\win.ini"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }
}
