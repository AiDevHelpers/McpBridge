using Xunit;

namespace AiDevHelpers.Tray.Tests;

public sealed class ProjectTests
{
    [Fact]
    public void AssemblyNameMatchesTheProject()
    {
        Assert.Equal("AiDevHelpers.Tray", typeof(ProjectMarker).Assembly.GetName().Name);
    }
}
