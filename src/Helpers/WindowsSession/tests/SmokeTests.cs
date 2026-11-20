using AiDevHelpers.Helpers.WindowsSession;
using Xunit;

namespace AiDevHelpers.Helpers.WindowsSession.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void UserSessionTypeIsAvailable()
    {
        Assert.Equal("UserSession", typeof(UserSession).Name);
    }
}
