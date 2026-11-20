using AiDevHelpers.Common.Contracts;
using Xunit;

namespace AiDevHelpers.Common.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void CommonContractsAreAvailable()
    {
        Assert.True(typeof(IIDELauncher).IsInterface);
    }
}
