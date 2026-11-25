using AiDevHelpers.Helpers.ProjectHelper;
using Xunit;

namespace AiDevHelpers.Helpers.ProjectHelper.Tests;

public sealed class SolutionLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "layout-" + Guid.NewGuid().ToString("N"));

    public SolutionLayoutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Solution(params string[] projectPaths)
    {
        var path = Path.Combine(_root, "Repo.slnx");
        var body = string.Join("\n", projectPaths.Select(p => $"    <Project Path=\"{p}\" />"));
        File.WriteAllText(path, $"<Solution>\n  <Folder Name=\"/Src/\">\n{body}\n  </Folder>\n</Solution>\n");
        return path;
    }

    [Fact]
    public void ThisRepoShapeIsInferred()
    {
        var sln = Solution(
            "src/Helpers/A/src/AiDevHelpers.Helpers.A.csproj",
            "src/Helpers/B/src/AiDevHelpers.Helpers.B.csproj",
            "src/Helpers/A/tests/AiDevHelpers.Helpers.A.Tests.csproj",
            "src/Helpers/B/tests/AiDevHelpers.Helpers.B.Tests.csproj");

        var layout = SolutionLayout.Infer(sln);

        Assert.Equal("src/Helpers/{name}/src", layout.SourceFolder);
        Assert.Equal("src/Helpers/{name}/tests", layout.TestFolder);
    }

    [Fact]
    public void FlatShapeIsInferred()
    {
        var sln = Solution("src/X/X.csproj", "src/Y/Y.csproj");

        Assert.Equal("src/{name}", SolutionLayout.Infer(sln).SourceFolder);
    }

    [Fact]
    public void SingleProjectGivesNoDefault()
    {
        var sln = Solution("src/Helpers/A/src/AiDevHelpers.Helpers.A.csproj");

        Assert.Null(SolutionLayout.Infer(sln).SourceFolder);
    }

    [Fact]
    public void DisagreeingShapesGiveNoDefault()
    {
        var sln = Solution("src/Helpers/A/src/A.csproj", "src/Flat/B/B.csproj", "tools/C/C.csproj");

        Assert.Null(SolutionLayout.Infer(sln).SourceFolder);
    }

    [Fact]
    public void MissingSolutionGivesNoDefault()
    {
        var layout = SolutionLayout.Infer(Path.Combine(_root, "missing.slnx"));

        Assert.Null(layout.SourceFolder);
        Assert.Null(layout.TestFolder);
    }
}
