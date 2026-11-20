using AiDevHelpers.MCP.Core;
using Xunit;

namespace AiDevHelpers.MCP.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void CoreLibraryIsReferenced()
    {
        Assert.Equal("AiDevHelpers.MCP.Core", typeof(McpServiceCollectionExtensions).Assembly.GetName().Name);
    }
}
