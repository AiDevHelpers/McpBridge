using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class ToolsetRoutingTests
{
    [Theory]
    [InlineData("container")]
    [InlineData("wsl")]
    [InlineData("model_server_status")]
    [InlineData("model_server_capacity")]
    public void StandaloneOnlyToolsAreRefusedOnTheIdePaths(string tool)
    {
        Assert.NotNull(ToolsetRouting.RefusalFor(tool, ToolsetPaths.VisualStudio));
        Assert.NotNull(ToolsetRouting.RefusalFor(tool, ToolsetPaths.VsCode));
    }

    [Theory]
    [InlineData("container")]
    [InlineData("wsl")]
    [InlineData("model_server_status")]
    public void StandaloneOnlyToolsRunOnTheStandalonePath(string tool)
    {
        Assert.Null(ToolsetRouting.RefusalFor(tool, ToolsetPaths.Standalone));
    }

    [Fact]
    public void TheRefusalNamesThePath()
    {
        var text = ToolsetRouting.RefusalFor("container", ToolsetPaths.VsCode);

        Assert.Contains("VsCode", text);
        Assert.Contains("container", text);
    }

    [Theory]
    [InlineData("ide_run_tests")]
    [InlineData("ide_find_in_files")]
    [InlineData("some_unknown_tool")]
    public void IdeToolsAreNeverRefusedByTheGate(string tool)
    {
        Assert.Null(ToolsetRouting.RefusalFor(tool, ToolsetPaths.VisualStudio));
        Assert.Null(ToolsetRouting.RefusalFor(tool, ToolsetPaths.VsCode));
    }

    [Fact]
    public void AMissingToolNameIsNotRefused()
    {
        Assert.Null(ToolsetRouting.RefusalFor(null, ToolsetPaths.VsCode));
    }
}
