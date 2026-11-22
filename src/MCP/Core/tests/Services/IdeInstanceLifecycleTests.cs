using AiDevHelpers.MCP.Core.Services;

using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class IdeInstanceLifecycleTests
{
    [Fact]
    public void LaunchArguments_PassOnlyTheSolution_SoDevenvStartsANewInstance()
    {
        // devenv /edit hands the file to a running instance, so a second solution never gets its own instance.
        var args = IdeInstanceLifecycle.LaunchArguments(@"C:\scratch\Scratch.sln");

        Assert.Equal([@"C:\scratch\Scratch.sln"], args);
    }
}
