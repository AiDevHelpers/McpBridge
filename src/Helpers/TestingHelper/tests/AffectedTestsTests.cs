using AiDevHelpers.Helpers.TestingHelper;
using Xunit;

namespace AiDevHelpers.Helpers.TestingHelper.Tests;

public sealed class AffectedTestsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "affected-tests", Guid.NewGuid().ToString("N"));

    public AffectedTestsTests()
    {
        // Common <- Lib <- App, with a test project for Lib and one for App, and an unrelated Other with its own test project.
        Project("src/Common/src/Common.csproj");
        Project("src/Lib/src/Lib.csproj", "../../Common/src/Common.csproj");
        Project("src/App/src/App.csproj", "../../Lib/src/Lib.csproj");
        Project("src/Lib/tests/Lib.Tests.csproj", "../src/Lib.csproj", isTest: true);
        Project("src/App/tests/App.Tests.csproj", "../src/App.csproj", isTest: true);
        Project("src/Other/src/Other.csproj");
        Project("src/Other/tests/Other.Tests.csproj", "../src/Other.csproj", isTest: true);
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Project(string relative, string? reference = null, bool isTest = false)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var refs = reference is null ? string.Empty : $"<ItemGroup><ProjectReference Include=\"{reference.Replace('/', '\\')}\" /></ItemGroup>";
        var test = isTest ? "<PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>" : string.Empty;
        File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\">{test}{refs}</Project>");
    }

    private IReadOnlyList<string> Names(params string[] changed) =>
        AffectedTests.Find(_root, changed).Select(p => Path.GetFileNameWithoutExtension(p)).ToList();

    [Fact]
    public void ChangeInALibraryRunsItsTestsAndTheTestsOfEverythingThatReferencesIt()
    {
        var names = Names("src/Lib/src/Thing.cs");

        Assert.Equal(["App.Tests", "Lib.Tests"], names);
    }

    [Fact]
    public void ChangeInTheBottomProjectRunsEveryDependentTest()
    {
        var names = Names("src/Common/src/Util.cs");

        Assert.Equal(["App.Tests", "Lib.Tests"], names);
    }

    [Fact]
    public void ChangeInATestProjectRunsOnlyThatProject()
    {
        Assert.Equal(["App.Tests"], Names("src/App/tests/AppTests.cs"));
    }

    [Fact]
    public void AllTestProjectsListsEveryTestProjectAndNoOthers()
    {
        var names = AffectedTests.AllTestProjects(_root).Select(p => Path.GetFileNameWithoutExtension(p)).ToList();

        Assert.Equal(["App.Tests", "Lib.Tests", "Other.Tests"], names);
    }

    [Fact]
    public void UnrelatedProjectsAreLeftOut()
    {
        Assert.DoesNotContain("Other.Tests", Names("src/Lib/src/Thing.cs"));
    }

    [Fact]
    public void ABuildWideFileRunsEveryTestProject()
    {
        Assert.Equal(["App.Tests", "Lib.Tests", "Other.Tests"], Names("Directory.Build.props"));
    }

    [Fact]
    public void AFileOutsideAnyProjectAffectsNothing()
    {
        Assert.Empty(Names("docs/readme.md"));
    }

    [Fact]
    public void AbsolutePathsAreAccepted()
    {
        var names = AffectedTests.Find(_root, [Path.Combine(_root, "src", "Other", "src", "X.cs")])
            .Select(p => Path.GetFileNameWithoutExtension(p)).ToList();

        Assert.Equal(["Other.Tests"], names);
    }
}
