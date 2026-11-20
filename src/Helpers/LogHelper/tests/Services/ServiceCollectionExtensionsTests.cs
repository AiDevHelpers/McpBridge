using Microsoft.Extensions.DependencyInjection;
using AiDevHelpers.Helpers.LogHelper.Contracts;
using AiDevHelpers.Helpers.LogHelper.Models;
using AiDevHelpers.Helpers.LogHelper.Services;
using Xunit;

namespace AiDevHelpers.Helpers.LogHelper.Tests.Services;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddLogHelper_RegistersLogServiceWithConfiguredOptions()
    {
        var services = new ServiceCollection();
        services.AddLogHelper(o => o.AllowedRoots = new[] { @"C:\logs" });

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ILogService>());
        Assert.Equal(new[] { @"C:\logs" }, provider.GetRequiredService<LogHelperOptions>().AllowedRoots);
    }
}
