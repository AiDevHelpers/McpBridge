using AiDevHelpers.Helpers.ExtensionsHelper;
using Xunit;

namespace AiDevHelpers.Helpers.ExtensionsHelper.Tests;

public sealed class ExtensionsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ext-service-tests", Guid.NewGuid().ToString("N"));

    private static readonly MarketplaceExtension Ext = new(
        Id: "pub.tool",
        Version: "2.0.0",
        DisplayName: "Tool",
        Publisher: "pub",
        Description: "A tool",
        VsixUrl: "https://example.test/tool.vsix");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task PlanAdd_VsCode_DoesNotRunAnything()
    {
        var runner = new FakeRunner();
        var svc = new ExtensionsService(runner, new FakeMarketplace(null), _root, () => null);

        var pending = await svc.PlanAddAsync(ExtensionIde.VsCode, "pub.tool");

        Assert.Equal("code --install-extension pub.tool", pending.Command);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ConfirmAdd_VsCode_RunsInstall()
    {
        var runner = new FakeRunner();
        var svc = new ExtensionsService(runner, new FakeMarketplace(null), _root, () => null);
        var pending = await svc.PlanAddAsync(ExtensionIde.VsCode, "pub.tool");

        var result = await svc.ConfirmAddAsync(pending.Token);

        Assert.True(result.Ok);
        Assert.Equal(new[] { "code --install-extension pub.tool" }, runner.Calls);
    }

    [Fact]
    public async Task ConfirmAdd_TokenUsedOnce()
    {
        var runner = new FakeRunner();
        var svc = new ExtensionsService(runner, new FakeMarketplace(null), _root, () => null);
        var pending = await svc.PlanAddAsync(ExtensionIde.VsCode, "pub.tool");
        await svc.ConfirmAddAsync(pending.Token);

        var second = await svc.ConfirmAddAsync(pending.Token);

        Assert.False(second.Ok);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task ConfirmAdd_UnknownToken_ReportsError()
    {
        var svc = new ExtensionsService(new FakeRunner(), new FakeMarketplace(null), _root, () => null);

        var result = await svc.ConfirmAddAsync("nope");

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task PlanAdd_VisualStudio_UnknownIdThrows()
    {
        var svc = new ExtensionsService(new FakeRunner(), new FakeMarketplace(null), _root, () => null);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.PlanAddAsync(ExtensionIde.VisualStudio, "pub.missing"));
    }

    [Fact]
    public async Task ConfirmAdd_VisualStudio_DownloadsThenRunsVsixInstaller()
    {
        var runner = new FakeRunner();
        var market = new FakeMarketplace(Ext);
        var installer = @"C:\VS\VSIXInstaller.exe";
        var svc = new ExtensionsService(runner, market, _root, () => installer);
        var pending = await svc.PlanAddAsync(ExtensionIde.VisualStudio, "pub.tool");
        Assert.Empty(market.Downloads);

        var result = await svc.ConfirmAddAsync(pending.Token);

        Assert.True(result.Ok);
        Assert.Equal(new[] { Ext.VsixUrl }, market.Downloads);
        var call = Assert.Single(runner.Calls);
        Assert.StartsWith(installer + " ", call);
        Assert.Contains(".vsix", call);
    }

    [Fact]
    public async Task ConfirmAdd_VisualStudio_NoInstallerReportsError()
    {
        var svc = new ExtensionsService(new FakeRunner(), new FakeMarketplace(Ext), _root, () => null);
        var pending = await svc.PlanAddAsync(ExtensionIde.VisualStudio, "pub.tool");

        var result = await svc.ConfirmAddAsync(pending.Token);

        Assert.False(result.Ok);
        Assert.Contains("VSIXInstaller", result.Output);
    }
}
