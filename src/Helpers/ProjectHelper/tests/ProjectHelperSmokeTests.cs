using Xunit;

namespace AiDevHelpers.Helpers.ProjectHelper.Tests;

public sealed class ProjectHelperSmokeTests
{
    [Fact]
    public void AssemblyLoads() => Assert.NotNull(typeof(ProjectHelperSmokeTests).Assembly);
}
