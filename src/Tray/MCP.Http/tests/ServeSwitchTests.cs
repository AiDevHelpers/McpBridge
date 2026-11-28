using AiDevHelpers.MCP.Http;
using Xunit;

namespace AiDevHelpers.MCP.Http.Tests;

public sealed class ServeSwitchTests
{
    // The HTTP server starts only when it is asked to serve (the service passes --serve at logon). Anything else, such as a stdio client
    // launching the program, must not open a listener.
    [Fact]
    public void ServesOnlyWhenTheSwitchIsGiven()
    {
        Assert.True(ServeSwitch.IsRequested(["--serve"]));
        Assert.True(ServeSwitch.IsRequested(["--Http:Port=6000", "--serve"]));
        Assert.False(ServeSwitch.IsRequested([]));
        Assert.False(ServeSwitch.IsRequested(["--Http:Port=6000"]));
    }

    [Fact]
    public void TheSwitchIsRemovedBeforeConfigurationReadsTheArguments()
    {
        var rest = ServeSwitch.Without(["--serve", "--Http:Port=6000"]);

        Assert.Equal(["--Http:Port=6000"], rest);
    }
}
