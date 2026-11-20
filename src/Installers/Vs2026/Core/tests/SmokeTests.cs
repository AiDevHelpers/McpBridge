using AiDevHelpers.Installers.Vs2026.Core;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void CoreTypesAreInExpectedNamespace()
    {
        Assert.Equal("AiDevHelpers.Installers.Vs2026.Core", typeof(BridgeHost).Namespace);
    }
}
