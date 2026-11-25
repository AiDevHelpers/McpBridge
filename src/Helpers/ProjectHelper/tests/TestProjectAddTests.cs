using AiDevHelpers.Common.Processes;
using AiDevHelpers.Helpers.ProjectHelper;
using Xunit;

namespace AiDevHelpers.Helpers.ProjectHelper.Tests;

public sealed class TestProjectAddTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "addtest-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDotnet _dotnet = new();
    private const string Solution = "AiDevHelpers.slnx";

    public TestProjectAddTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".aidevhelpers"));
        File.WriteAllText(Path.Combine(_root, ".aidevhelpers", "project-standards.json"), """
        {
          "baseFramework": "net10.0",
          "properties": { "Nullable": "enable" },
          "analyzers": [ "Foo.Analyzers" ]
        }
        """);
        Csproj("src/Core/Core.csproj", "<Nullable>enable</Nullable>", "<PackageReference Include=\"Foo.Analyzers\" Version=\"1.0.0\" />");
        Csproj("src/Other/tests/Other.Tests.csproj", "<Nullable>enable</Nullable>",
            "<PackageReference Include=\"Foo.Analyzers\" Version=\"1.0.0\" /><PackageReference Include=\"xunit\" Version=\"2.9.3\" /><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.0.0\" />");
        Csproj("src/Extra/tests/Extra.Tests.csproj", "<Nullable>enable</Nullable>",
            "<PackageReference Include=\"Foo.Analyzers\" Version=\"1.0.0\" /><PackageReference Include=\"xunit\" Version=\"2.9.3\" /><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.0.0\" />");
        WriteSolution(
            "  <Folder Name=\"/Core/\">\n    <Project Path=\"src/Core/Core.csproj\" />\n  </Folder>\n"
            + "  <Folder Name=\"/Tests/\">\n    <Project Path=\"src/Other/tests/Other.Tests.csproj\" />\n    <Project Path=\"src/Extra/tests/Extra.Tests.csproj\" />\n  </Folder>\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteSolution(string body) =>
        File.WriteAllText(Path.Combine(_root, Solution), "<Solution>\n" + body + "</Solution>\n");

    private void Csproj(string relative, string properties, string items)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    " + properties
            + "\n  </PropertyGroup>\n  <ItemGroup>" + items + "</ItemGroup>\n</Project>\n");
    }

    private Task<AddPlanResult> PlanAsync(string? folder = "src/Core/tests", bool seed = false) =>
        TestProjectAdder.PlanAsync(_root, Solution, "src/Core/Core.csproj", folder, "/Core/", seed, _dotnet.Run, CancellationToken.None);

    [Fact]
    public async Task PlanNamesTheTestProjectReferencesTheSourceAndAddsTheRunner()
    {
        var plan = await PlanAsync();

        Assert.NotNull(plan.Token);
        Assert.Empty(plan.Errors);
        Assert.Equal("src/Core/tests/Core.Tests.csproj", plan.ProjectPath);
        Assert.Contains(plan.Edits, a => a.Kind == FixKind.AddReference && a.Value == "../Core.csproj");
        Assert.Contains(plan.Edits, a => a.Kind == FixKind.AddPackage && a.Name == "Microsoft.NET.Test.Sdk" && a.Value == "18.0.0");
        Assert.Contains(plan.Edits, a => a.Kind == FixKind.AddPackage && a.Name == "xunit" && a.Value == "2.9.3");
        Assert.False(Directory.Exists(Path.Combine(_root, "src", "Core", "tests")));
    }

    [Fact]
    public async Task FolderDefaultsToTheInferredTestLayout()
    {
        var plan = await PlanAsync(folder: null);

        Assert.Equal("src/Core/tests/Core.Tests.csproj", plan.ProjectPath);
        Assert.Empty(plan.Errors);
    }

    [Fact]
    public async Task FolderIsRequiredWhenTheSolutionGivesNoLayout()
    {
        WriteSolution("  <Folder Name=\"/Core/\">\n    <Project Path=\"src/Core/Core.csproj\" />\n  </Folder>\n");

        var plan = await PlanAsync(folder: null);

        Assert.Null(plan.Token);
        Assert.Contains(plan.Errors, e => e.Contains("Folder"));
    }

    [Fact]
    public async Task ExistingTestProjectIsRefused()
    {
        WriteSolution("  <Folder Name=\"/Core/\">\n    <Project Path=\"src/Core/Core.csproj\" />\n    <Project Path=\"src/Core/tests/Core.Tests.csproj\" />\n  </Folder>\n");

        var plan = await PlanAsync();

        Assert.Null(plan.Token);
        Assert.NotEmpty(plan.Errors);
    }

    [Fact]
    public async Task ConfirmWritesTheReferenceTheSeedTestAndTheSolutionEntry()
    {
        var plan = await PlanAsync(seed: true);

        var result = await ProjectAdder.ConfirmAsync(_root, plan.Token!, _dotnet.Run, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        var csproj = File.ReadAllText(Path.Combine(_root, "src", "Core", "tests", "Core.Tests.csproj"));
        Assert.Contains("<ProjectReference Include=\"../Core.csproj\" />", csproj);
        Assert.Contains("Microsoft.NET.Test.Sdk", csproj);
        var seed = File.ReadAllText(Path.Combine(_root, "src", "Core", "tests", "SeedTest.cs"));
        Assert.Contains("Assert.Fail", seed);
        Assert.Contains("<Project Path=\"src/Core/tests/Core.Tests.csproj\" />", File.ReadAllText(Path.Combine(_root, Solution)));
    }

    [Fact]
    public async Task BuildFailureRemovesTheTestProjectAndRestoresTheSolution()
    {
        var before = File.ReadAllBytes(Path.Combine(_root, Solution));
        var plan = await PlanAsync();
        _dotnet.BuildExit = 1;

        var result = await ProjectAdder.ConfirmAsync(_root, plan.Token!, _dotnet.Run, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(Directory.Exists(Path.Combine(_root, "src", "Core", "tests")));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_root, Solution)));
    }
}
