using AiDevHelpers.Installers.Vs2026.Core;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Installer.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void InstallerReferencesCore()
    {
        Assert.NotNull(BridgeLog.ReadAll());
    }
}
