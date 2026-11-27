using System.Runtime.Versioning;
using Xunit;

namespace AiDevHelpers.Helpers.WindowsSession.Tests;

[SupportedOSPlatform("windows")]
public sealed class UserSessionTests
{
    [Fact]
    public void ChooseConsoleUserSessionReturnsTheConsoleSessionWhenItHasAUser()
    {
        Assert.Equal(1u, UserSession.ChooseConsoleUserSession(1u, id => id == 1u));
    }

    [Fact]
    public void ChooseConsoleUserSessionReturnsNoSessionWhenTheConsoleHasNoUser()
    {
        Assert.Equal(UserSession.NoSession, UserSession.ChooseConsoleUserSession(1u, _ => false));
    }

    [Fact]
    public void ChooseConsoleUserSessionReturnsNoSessionWhenThereIsNoConsole()
    {
        Assert.Equal(UserSession.NoSession, UserSession.ChooseConsoleUserSession(UserSession.NoSession, _ => true));
    }
}
