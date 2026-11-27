using Xunit;

namespace AiDevHelpers.Host.Tests;

public sealed class ProjectTests
{
    [Fact]
    public void AssemblyNameMatchesTheProject()
    {
        Assert.Equal("AiDevHelpers.Host", typeof(ProjectMarker).Assembly.GetName().Name);
    }

    [Fact]
    public void TheFirstStartHasNoDelayAndTheDelaysGrowThenCap()
    {
        Assert.Equal(TimeSpan.Zero, RestartBackoff.Next(0));
        Assert.Equal(TimeSpan.FromSeconds(1), RestartBackoff.Next(1));
        Assert.Equal(TimeSpan.FromSeconds(2), RestartBackoff.Next(2));
        Assert.Equal(TimeSpan.FromSeconds(60), RestartBackoff.Next(7));
        Assert.Equal(TimeSpan.FromSeconds(60), RestartBackoff.Next(RestartBackoff.MaxFailures));
    }

    [Fact]
    public void TheSupervisorGivesUpAfterTooManyFailuresInARow()
    {
        Assert.Null(RestartBackoff.Next(RestartBackoff.MaxFailures + 1));
    }

    [Fact]
    public void TheConsoleSessionWinsWhenAUserIsOnIt()
    {
        var session = SessionChoice.Choose(1, s => s == 1, () => 3);

        Assert.Equal(1u, session);
    }

    [Fact]
    public void AnRdpUserIsUsedWhenTheConsoleHasNoUser()
    {
        var session = SessionChoice.Choose(1, _ => false, () => 3);

        Assert.Equal(3u, session);
    }
}
