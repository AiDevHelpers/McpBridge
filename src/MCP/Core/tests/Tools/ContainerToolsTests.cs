using System.Text.Json;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

/// <summary>Refusal paths only: nothing here starts docker or podman.</summary>
public sealed class ContainerToolsTests
{
    [Fact]
    public async Task Unknown_action_is_refused()
    {
        using var doc = JsonDocument.Parse(await ContainerTools.Container("explode", "web"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Unknown action", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Invalid_name_is_refused_before_any_runtime_call()
    {
        using var doc = JsonDocument.Parse(await ContainerTools.Container("status", "-rm; evil"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Invalid container name", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Action_is_trimmed_and_case_insensitive()
    {
        using var doc = JsonDocument.Parse(await ContainerTools.Container("  EXEC ", "-bad", command: "ls"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Invalid container name", doc.RootElement.GetProperty("error").GetString());
    }
}
