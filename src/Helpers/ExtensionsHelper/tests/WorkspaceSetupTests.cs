using System.Text.Json.Nodes;
using AiDevHelpers.Helpers.ExtensionsHelper;
using Xunit;

namespace AiDevHelpers.Helpers.ExtensionsHelper.Tests;

public sealed class WorkspaceSetupTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ext-workspace-tests", Guid.NewGuid().ToString("N"));

    public WorkspaceSetupTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private string Sln => Path.Combine(_folder, "App.sln");

    private string Extensions => Path.Combine(_folder, ".vscode", "extensions.json");

    private string Settings => Path.Combine(_folder, ".vscode", "settings.json");

    private static FakeRunner RunnerWithInstalled(params string[] ids) => new()
    {
        Handler = (_, _) => new CommandResult(0, string.Join("\n", ids.Select(id => id + "@1.0.0")), string.Empty),
    };

    [Fact]
    public async Task RecommendWritesBothCSharpExtensionsWhenNoFileExists()
    {
        var setup = new WorkspaceSetup(RunnerWithInstalled());

        var result = await setup.SetupAsync(_folder, Sln);

        Assert.True(result.Ok);
        var recs = JsonNode.Parse(File.ReadAllText(Extensions))!["recommendations"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("ms-dotnettools.csdevkit", recs);
        Assert.Contains("ms-dotnettools.csharp", recs);
    }

    [Fact]
    public async Task RecommendPreservesExistingEntriesWithoutDuplicates()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Extensions)!);
        File.WriteAllText(Extensions,
            "{ \"recommendations\": [\"ms-dotnettools.csharp\", \"esbenp.prettier-vscode\"], \"unwantedRecommendations\": [] }");
        var setup = new WorkspaceSetup(RunnerWithInstalled());

        await setup.SetupAsync(_folder, Sln);

        var root = JsonNode.Parse(File.ReadAllText(Extensions))!;
        var recs = root["recommendations"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(3, recs.Count);
        Assert.Contains("esbenp.prettier-vscode", recs);
        Assert.NotNull(root["unwantedRecommendations"]);
    }

    [Fact]
    public async Task ApplyWritesDefaultSolutionWhenCSharpDevKitInstalled()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
        File.WriteAllText(Settings, "{ \"editor.tabSize\": 4 }");
        var setup = new WorkspaceSetup(RunnerWithInstalled("ms-dotnettools.csdevkit"));

        var result = await setup.SetupAsync(_folder, Sln);

        Assert.True(result.SettingApplied);
        var root = JsonNode.Parse(File.ReadAllText(Settings))!;
        Assert.Equal(Sln, root["dotnet.defaultSolution"]!.GetValue<string>());
        Assert.Equal(4, root["editor.tabSize"]!.GetValue<int>());
    }

    [Fact]
    public async Task ApplyWritesNothingWhenCSharpDevKitNotInstalled()
    {
        var setup = new WorkspaceSetup(RunnerWithInstalled("ms-dotnettools.csharp"));

        var result = await setup.SetupAsync(_folder, Sln);

        Assert.False(result.SettingApplied);
        Assert.False(File.Exists(Settings));
    }

    [Fact]
    public async Task MalformedExtensionsFileIsReportedAndLeftUntouched()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Extensions)!);
        const string broken = "{ not json";
        File.WriteAllText(Extensions, broken);
        var setup = new WorkspaceSetup(RunnerWithInstalled());

        var result = await setup.SetupAsync(_folder, Sln);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Equal(broken, File.ReadAllText(Extensions));
    }
}
