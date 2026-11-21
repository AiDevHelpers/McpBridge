using AiDevHelpers.Helpers.ExtensionsHelper;
using Xunit;

namespace AiDevHelpers.Helpers.ExtensionsHelper.Tests;

public sealed class MarketplaceAndLocatorTests : IDisposable
{
    // Shape the parser reads. Not captured from the live Marketplace endpoint; verify before relying on it.
    private const string Response =
        "{ \"results\": [ { \"extensions\": [ { \"extensionName\": \"tool\", " +
        "\"publisher\": { \"publisherName\": \"pub\", \"displayName\": \"Pub\" }, " +
        "\"shortDescription\": \"A tool\", \"displayName\": \"Tool\", " +
        "\"versions\": [ { \"version\": \"2.0.0\", \"files\": [ { \"assetType\": \"Microsoft.VisualStudio.Services.VSIXPackage\", " +
        "\"source\": \"https://example.test/tool.vsix\" } ] } ] } ] } ] }";

    private readonly string _pf = Path.Combine(Path.GetTempPath(), "ext-locator-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_pf))
        {
            Directory.Delete(_pf, recursive: true);
        }
    }

    [Fact]
    public void ParsesFirstMatchingExtensionFromResponse()
    {
        var ext = MarketplaceResponse.ParseExtension(Response, "pub.tool");

        Assert.NotNull(ext);
        Assert.Equal("pub.tool", ext!.Id);
        Assert.Equal("2.0.0", ext.Version);
        Assert.Equal("https://example.test/tool.vsix", ext.VsixUrl);
    }

    [Fact]
    public void ParseReturnsNullWhenIdNotInResponse()
    {
        Assert.Null(MarketplaceResponse.ParseExtension(Response, "pub.other"));
    }

    [Fact]
    public void LocatorPicksNewestVisualStudioInstaller()
    {
        foreach (var version in new[] { "17.0", "18.0" })
        {
            var dir = Path.Combine(_pf, "Microsoft Visual Studio", version, "Enterprise", "Common7", "IDE");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "VSIXInstaller.exe"), string.Empty);
        }

        var found = VsixInstallerLocator.Find(new[] { _pf });

        Assert.NotNull(found);
        Assert.Contains(Path.Combine("18.0"), found!);
    }

    [Fact]
    public void LocatorReturnsNullWhenMissing()
    {
        Assert.Null(VsixInstallerLocator.Find(new[] { _pf }));
    }
}
