using AiDevHelpers.Host;
using Xunit;

namespace AiDevHelpers.Host.Tests;

public sealed class SupervisorPolicyTests
{
    // At boot there is no logged-on user for a while. Waiting for one is not a failure, so the supervisor must never give up on it.
    [Fact]
    public void WaitingForAUserSessionNeverCountsAsAFailure()
    {
        var failures = RestartBackoff.MaxFailures;

        var (after, delay) = SupervisorPolicy.AfterNoSession(failures);

        Assert.Equal(failures, after);
        Assert.True(delay > TimeSpan.Zero);
    }

    [Fact]
    public void TheNoSessionWaitIsFixedAndShort()
    {
        var (_, delay) = SupervisorPolicy.AfterNoSession(0);

        Assert.Equal(TimeSpan.FromSeconds(5), delay);
    }
}
