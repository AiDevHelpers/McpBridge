using Xunit;

namespace AiDevHelpers.Common.Tests;

public sealed class ElevationPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "policy-" + Guid.NewGuid().ToString("N"));

    public ElevationPolicyTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Solution(string name = "App.sln", string body = "")
    {
        var path = Path.Combine(_root, "src", name);
        File.WriteAllText(path, body);
        return path;
    }

    private static RepoElevation Elevation(ElevationMode mode, params string[] projects) =>
        new(mode, [.. projects.Select(p => new ElevatedProject(p, null))], [], []);

    private LaunchDecision Decide(string solution, RepoElevation elevation, params string[] solutionProjects) =>
        ElevationPolicy.Decide(solution, _root, elevation, _ => solutionProjects);

    [Fact]
    public void ASolutionWithAListedProject_IsLaunchedElevated()
    {
        var decision = Decide(Solution(), Elevation(ElevationMode.Listed, "src/Drv/Drv.csproj"), "Drv/Drv.csproj", "Ok/Ok.csproj");

        Assert.True(decision.Elevated);
        Assert.Contains("src/Drv/Drv.csproj", decision.Reason);
    }

    [Fact]
    public void ASolutionWithNoListedProject_IsLaunchedNormally()
    {
        Assert.False(Decide(Solution(), Elevation(ElevationMode.Listed, "src/Drv/Drv.csproj"), "Ok/Ok.csproj").Elevated);
    }

    [Fact]
    public void NothingListed_NeverElevates_OnAGuess()
    {
        Assert.False(Decide(Solution(), Elevation(ElevationMode.Listed), "Drv/Drv.csproj").Elevated);
    }

    [Fact]
    public void Always_Elevates_AndNeverWinsOverTheList()
    {
        Assert.True(Decide(Solution(), Elevation(ElevationMode.Always)).Elevated);
        Assert.False(Decide(Solution(), Elevation(ElevationMode.Never, "src/Drv/Drv.csproj"), "Drv/Drv.csproj").Elevated);
    }

    [Fact]
    public void ListedProjectsAreMatchedByRepoRelativePath_IgnoringCaseAndSeparators()
    {
        Assert.True(Decide(Solution(), Elevation(ElevationMode.Listed, "SRC/drv/DRV.csproj"), @"Drv\Drv.csproj").Elevated);
        Assert.False(Decide(Solution(), Elevation(ElevationMode.Listed, "other/Drv/Drv.csproj"), "Drv/Drv.csproj").Elevated);
    }

    [Fact]
    public void FromDisk_ReadsTheFilesAndTheSolution()
    {
        var solution = Solution("App.slnx", """<Solution><Project Path="Drv/Drv.csproj" /></Solution>""");
        Directory.CreateDirectory(Path.Combine(_root, ".aidevtools"));
        ElevationFiles.AddProject(_root, "src/Drv/Drv.csproj", "manifest");

        Assert.True(ElevationPolicy.Decide(solution).Elevated);

        ElevationFiles.SetMode(_root, ElevationMode.Never);
        Assert.False(ElevationPolicy.Decide(solution).Elevated);
    }

    [Fact]
    public void ASolutionOutsideGit_IsLaunchedNormally()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nogit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "A.sln");
            File.WriteAllText(path, "");

            Assert.False(ElevationPolicy.Decide(path).Elevated);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void SolutionProjects_ReadsSlnAndSlnx_AndNothingFromBrokenFiles()
    {
        var sln = Solution("A.sln", "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Drv\", \"Drv\\Drv.csproj\", \"{1}\"\nEndProject\n");
        var slnx = Solution("B.slnx", """<Solution><Project Path="X/X.csproj" /></Solution>""");
        var broken = Solution("C.slnx", "<Solution");

        Assert.Equal(["Drv/Drv.csproj"], SolutionProjects.Read(sln));
        Assert.Equal(["X/X.csproj"], SolutionProjects.Read(slnx));
        Assert.Empty(SolutionProjects.Read(broken));
        Assert.Empty(SolutionProjects.Read(Path.Combine(_root, "missing.sln")));
    }
}
