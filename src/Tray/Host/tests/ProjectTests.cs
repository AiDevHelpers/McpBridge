using Xunit;

namespace AiDevHelpers.Host.Tests;

public sealed class ProjectTests
{
    [Fact]
    public void AssemblyNameMatchesTheProject()
    {
        Assert.Equal("AiDevHelpers.Host", typeof(ProjectMarker).Assembly.GetName().Name);
    }
}
