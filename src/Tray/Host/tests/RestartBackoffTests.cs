using Xunit;

namespace AiDevHelpers.Host.Tests;

public sealed class RestartBackoffTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 32)]
    [InlineData(7, 60)]
    [InlineData(50, 60)]
    public void TheDelayDoublesAndStopsAtOneMinute(int consecutiveFailures, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RestartBackoff.For(consecutiveFailures));
    }
}
