using Xunit;

namespace AiDevHelpers.MCP.Http.Tests;

public sealed class ProjectTests
{
    [Fact]
    public void AssemblyNameMatchesTheProject()
    {
        Assert.Equal("AiDevHelpers.MCP.Http", typeof(ProjectMarker).Assembly.GetName().Name);
    }
}
