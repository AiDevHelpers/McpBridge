using System.Text.Json;
using Xunit;

namespace AiDevHelpers.Helpers.ExtensionsHelper.Tests;

public sealed class BuildEnvironmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "build-env-tests", Guid.NewGuid().ToString("N"));

    public BuildEnvironmentTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Install(string name, bool msbuild, bool vssdk)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        if (msbuild)
        {
            var exe = Path.Combine(dir, "MSBuild", "Current", "Bin", "MSBuild.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllText(exe, "");
        }

        if (vssdk)
        {
            var targets = Path.Combine(dir, "MSBuild", "Microsoft", "VisualStudio", "v18.0", "VSSDK", "Microsoft.VsSDK.targets");
            Directory.CreateDirectory(Path.GetDirectoryName(targets)!);
            File.WriteAllText(targets, "");
        }

        return dir;
    }

    private static FakeRunner VsWhere(params object[] installs) => new()
    {
        Handler = (_, _) => new CommandResult(0, JsonSerializer.Serialize(installs), string.Empty),
    };

    private static object Entry(string path, string name, string version, bool prerelease) =>
        new { installationPath = path, displayName = name, installationVersion = version, isPrerelease = prerelease };

    [Fact]
    public async Task ListsInstallsWithMsBuildAndVsSdkTargets()
    {
        var community = Install("Community", msbuild: true, vssdk: true);
        var runner = VsWhere(Entry(community, "Visual Studio Community 2026", "18.1.0", false));

        var installs = await new BuildEnvironment(runner, "vswhere.exe").ListAsync();

        var install = Assert.Single(installs);
        Assert.Equal(community, install.Path);
        Assert.Equal("Visual Studio Community 2026", install.DisplayName);
        Assert.Equal("18.1.0", install.Version);
        Assert.False(install.IsPrerelease);
        Assert.EndsWith(Path.Combine("Bin", "MSBuild.exe"), install.MsBuildPath);
        Assert.EndsWith("Microsoft.VsSDK.targets", install.VsSdkTargetsPath);
    }

    [Fact]
    public async Task VsSdkTargetsAreNullWhenTheWorkloadIsMissing()
    {
        var insiders = Install("Insiders", msbuild: true, vssdk: false);
        var runner = VsWhere(Entry(insiders, "Visual Studio Insiders", "18.2.0", true));

        var install = Assert.Single(await new BuildEnvironment(runner, "vswhere.exe").ListAsync());

        Assert.NotNull(install.MsBuildPath);
        Assert.Null(install.VsSdkTargetsPath);
        Assert.True(install.IsPrerelease);
    }

    [Fact]
    public async Task AsksVsWhereForEveryInstallIncludingPrerelease()
    {
        var runner = VsWhere();

        await new BuildEnvironment(runner, "vswhere.exe").ListAsync();

        var call = Assert.Single(runner.Calls);
        Assert.Contains("-all", call);
        Assert.Contains("-prerelease", call);
        Assert.Contains("json", call);
    }

    [Fact]
    public async Task EmptyOutputMeansNoInstalls()
    {
        var runner = new FakeRunner { Handler = (_, _) => new CommandResult(0, "", "") };

        Assert.Empty(await new BuildEnvironment(runner, "vswhere.exe").ListAsync());
    }

    [Fact]
    public async Task VsWhereFailureThrowsWithItsMessage()
    {
        var runner = new FakeRunner { Handler = (_, _) => new CommandResult(1, "", "vswhere broke") };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new BuildEnvironment(runner, "vswhere.exe").ListAsync());

        Assert.Contains("vswhere broke", ex.Message);
    }
}
