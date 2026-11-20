using AiDevHelpers.Helpers.ExtensionsHelper;
using Xunit;

namespace AiDevHelpers.Helpers.ExtensionsHelper.Tests;

public sealed class VsCodeExtensionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ext-vscode-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ListParsesIdAtVersionLines()
    {
        var runner = new FakeRunner
        {
            Handler = (_, _) => new CommandResult(0, "ms-dotnettools.csharp@2.1.0\npub.tool@1.0.0\n", string.Empty),
        };
        var svc = new ExtensionsService(runner, new FakeMarketplace(null), _root, () => null);

        var list = await svc.ListVsCodeAsync();

        Assert.Equal(2, list.Count);
        Assert.Equal("ms-dotnettools.csharp", list[0].Id);
        Assert.Equal("2.1.0", list[0].Version);
        Assert.Equal(new[] { "code --list-extensions --show-versions" }, runner.Calls);
    }

    [Fact]
    public void GetInfoReadsPackageJsonOfInstalledVersion()
    {
        var folder = Path.Combine(_root, "pub.tool-1.0.0");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "package.json"),
            "{ \"name\": \"tool\", \"displayName\": \"Tool\", \"publisher\": \"pub\", \"version\": \"1.0.0\", \"description\": \"A tool\" }");
        var svc = new ExtensionsService(new FakeRunner(), new FakeMarketplace(null), _root, () => null);

        var info = svc.GetVsCodeInfo("pub.tool");

        Assert.NotNull(info);
        Assert.Equal("Tool", info!.DisplayName);
        Assert.Equal("1.0.0", info.Version);
        Assert.Equal("A tool", info.Description);
    }

    [Fact]
    public void GetInfoUnknownIdReturnsNull()
    {
        Directory.CreateDirectory(_root);
        var svc = new ExtensionsService(new FakeRunner(), new FakeMarketplace(null), _root, () => null);

        Assert.Null(svc.GetVsCodeInfo("pub.missing"));
    }
}
