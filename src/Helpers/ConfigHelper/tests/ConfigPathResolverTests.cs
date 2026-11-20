using AiDevHelpers.Helpers.ConfigHelper;
using Xunit;

namespace AiDevHelpers.Helpers.ConfigHelper.Tests;

public sealed class ConfigPathResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "config-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private ConfigPathOptions Options(string? solutionPath = null) => new()
    {
        SolutionPath = solutionPath,
        UserRoot = Dir("user"),
        AppDataRoot = Dir("user", "AppData", "Roaming"),
        LocalAppDataRoot = Dir("user", "AppData", "Local"),
        RemoteDebuggerSearchRoots = [Dir("ProgramFiles")],
    };

    [Fact]
    public void LaunchSettingsResolvesUnderProjectProperties()
    {
        var project = Dir("repo", "App");
        Directory.CreateDirectory(Path.Combine(project, "Properties"));
        File.WriteAllText(Path.Combine(project, "Properties", "launchSettings.json"), "{}");
        var resolver = new ConfigPathResolver(Options());

        var result = resolver.Resolve(ConfigFileKind.LaunchSettings, Path.Combine(project, "App.csproj"));

        Assert.True(result.Exists);
        Assert.Equal(Path.Combine(project, "Properties", "launchSettings.json"), result.Path);
        Assert.Null(result.Error);
    }

    [Fact]
    public void AppSettingsResolvesBesideProject()
    {
        var project = Dir("repo", "Api");
        File.WriteAllText(Path.Combine(project, "appsettings.json"), "{}");
        var resolver = new ConfigPathResolver(Options());

        var result = resolver.Resolve(ConfigFileKind.AppSettings, Path.Combine(project, "Api.csproj"));

        Assert.True(result.Exists);
        Assert.Equal(Path.Combine(project, "appsettings.json"), result.Path);
    }

    [Fact]
    public void MissingFileReportsNotExistsWithPath()
    {
        var project = Dir("repo", "Empty");
        var resolver = new ConfigPathResolver(Options());

        var result = resolver.Resolve(ConfigFileKind.AppSettings, Path.Combine(project, "Empty.csproj"));

        Assert.False(result.Exists);
        Assert.Equal(Path.Combine(project, "appsettings.json"), result.Path);
    }

    [Fact]
    public void SolutionSettingsPrefersVsconfigBesideSolution()
    {
        var repo = Dir("repo");
        var sln = Path.Combine(repo, "App.sln");
        File.WriteAllText(Path.Combine(repo, ".vsconfig"), "{}");
        var resolver = new ConfigPathResolver(Options(sln));

        var result = resolver.Resolve(ConfigFileKind.SolutionSettings);

        Assert.True(result.Exists);
        Assert.Equal(Path.Combine(repo, ".vsconfig"), result.Path);
    }

    [Fact]
    public void SolutionSettingsWithoutSolutionReturnsError()
    {
        var resolver = new ConfigPathResolver(Options());

        var result = resolver.Resolve(ConfigFileKind.SolutionSettings);

        Assert.False(result.Exists);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void UserSettingsPrefersVsCodeSettingsWhenPresent()
    {
        var opts = Options();
        var vscode = Path.Combine(opts.AppDataRoot, "Code", "User", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(vscode)!);
        File.WriteAllText(vscode, "{}");
        var resolver = new ConfigPathResolver(opts);

        var result = resolver.Resolve(ConfigFileKind.UserSettings);

        Assert.True(result.Exists);
        Assert.Equal(vscode, result.Path);
    }

    [Fact]
    public void RemoteDebuggerFindsConfigUnderSearchRoot()
    {
        var opts = Options();
        var debugger = Path.Combine(opts.RemoteDebuggerSearchRoots[0], "2026", "Common7", "IDE", "Remote Debugger");
        Directory.CreateDirectory(debugger);
        var file = Path.Combine(debugger, "msvsmon.config.xml");
        File.WriteAllText(file, "<Debugger />");
        var resolver = new ConfigPathResolver(opts);

        var result = resolver.Resolve(ConfigFileKind.RemoteDebugger);

        Assert.True(result.Exists);
        Assert.Equal(file, result.Path);
    }

    [Fact]
    public void RemoteDebuggerNotFoundReportsError()
    {
        var resolver = new ConfigPathResolver(Options());

        var result = resolver.Resolve(ConfigFileKind.RemoteDebugger);

        Assert.False(result.Exists);
        Assert.NotNull(result.Error);
    }
}
