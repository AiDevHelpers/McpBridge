using System.Text.Json;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

/// <summary>Refusal paths only: nothing here starts wsl.exe.</summary>
public sealed class WslToolsTests
{
    [Fact]
    public async Task Unknown_action_is_refused()
    {
        using var doc = JsonDocument.Parse(await WslTools.Wsl("explode"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Unknown action", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Run_without_command_is_refused()
    {
        using var doc = JsonDocument.Parse(await WslTools.Wsl("run", command: "  "));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Invalid_distro_name_is_refused()
    {
        using var doc = JsonDocument.Parse(await WslTools.Wsl("health", distro: "--import"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Invalid distro name", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Unknown_debug_runtime_is_refused()
    {
        using var doc = JsonDocument.Parse(await WslTools.Wsl("debug_setup", runtime: "ruby", programPath: "/x"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }
}
