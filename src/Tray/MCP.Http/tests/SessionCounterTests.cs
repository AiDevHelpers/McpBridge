using Xunit;

namespace AiDevHelpers.MCP.Http.Tests;

public sealed class SessionCounterTests
{
    [Fact]
    public void TheCountGoesUpAndDownWithSessions()
    {
        var counter = new SessionCounter();

        counter.Enter();
        counter.Enter();
        counter.Exit();

        Assert.Equal(1, counter.Count);
    }
}

