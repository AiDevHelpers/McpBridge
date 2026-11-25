using System.Text.Json;
using AiDevHelpers.Common;
using AiDevHelpers.Common.Contracts;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class RepoSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sh-setup-" + Guid.NewGuid().ToString("N"));

    public RepoSetupTests()
    {
        Directory.CreateDirectory(_root);
        Write("src/Drv/Drv.csproj", "<Project />");
        Write("src/Drv/app.manifest", """<x><requestedExecutionLevel level="requireAdministrator" /></x>""");
        Write("src/Tool/Tool.csproj", "<Project />");
        Write("src/Tool/app.manifest", """<x><requestedExecutionLevel level="requireAdministrator" /></x>""");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private sealed class Answers(params bool[] yes) : IConfirmer
    {
        private readonly Queue<bool> _yes = new(yes);

        public List<string> Asked { get; } = [];

        public bool Confirm(string prompt)
        {
            Asked.Add(prompt);
            return _yes.Count > 0 && _yes.Dequeue();
        }
    }

    [Fact]
    public void Scan_ReportsFindingsAndPendingProjects_AndWritesNothing()
    {
        var report = RepoSetup.Scan(_root);

        Assert.Equal(["src/Drv/Drv.csproj", "src/Tool/Tool.csproj"], report.PendingProjects);
        Assert.Equal(2, report.Findings.Count);
        Assert.False(Directory.Exists(Path.Combine(_root, ".aidevhelpers")));
        Assert.False(File.Exists(Path.Combine(_root, ".gitignore")));
    }

    [Fact]
    public void Scan_LeavesOutProjectsThatAreListedOrDeclined()
    {
        ElevationFiles.AddProject(_root, "src/Drv/Drv.csproj", "x");
        ElevationFiles.Decline(_root, "src/Tool/Tool.csproj");

        Assert.Empty(RepoSetup.Scan(_root).PendingProjects);
    }

    [Fact]
    public void Setup_YesListsTheProject_NoDeclinesIt_AndCreatesTheFiles()
    {
        var answers = new Answers(true, false, false);
        var output = new StringWriter();

        var exit = RepoSetup.Setup(_root, output, answers);

        Assert.Equal(0, exit);
        var elevation = ElevationFiles.Read(_root);
        Assert.Equal("src/Drv/Drv.csproj", Assert.Single(elevation.Projects).Path);
        Assert.Contains("requireAdministrator", elevation.Projects[0].Reason);
        Assert.Equal(["src/Tool/Tool.csproj"], elevation.Declined);
        Assert.Equal(ElevationMode.Listed, elevation.Mode);
        Assert.Contains(".aidevhelpers/user.json", File.ReadAllLines(Path.Combine(_root, ".gitignore")));
        Assert.Equal(3, answers.Asked.Count);
    }

    [Fact]
    public void Setup_AlwaysYesSetsTheModeToAlways()
    {
        var exit = RepoSetup.Setup(_root, new StringWriter(), new Answers(true, true, true));

        Assert.Equal(0, exit);
        Assert.Equal(ElevationMode.Always, ElevationFiles.Read(_root).Mode);
    }

    [Fact]
    public void Setup_AnAnswerOfNoEverywhere_ListsNothingAndKeepsTheModeListed()
    {
        RepoSetup.Setup(_root, new StringWriter(), new Answers());

        var elevation = ElevationFiles.Read(_root);
        Assert.Empty(elevation.Projects);
        Assert.Equal(2, elevation.Declined.Count);
        Assert.Equal(ElevationMode.Listed, elevation.Mode);
    }

    [Fact]
    public void Setup_ASecondRunOnlyAsksAboutWhatIsNew()
    {
        RepoSetup.Setup(_root, new StringWriter(), new Answers(true, false, false));
        Write("src/New/New.csproj", "<Project />");
        Write("src/New/app.manifest", """<x><requestedExecutionLevel level="requireAdministrator" /></x>""");

        var answers = new Answers(false);
        RepoSetup.Setup(_root, new StringWriter(), answers);

        // One question for the new project; the mode question is asked only while projects are listed and the mode is still 'listed'.
        Assert.Contains(answers.Asked, a => a.Contains("src/New/New.csproj"));
        Assert.DoesNotContain(answers.Asked, a => a.Contains("src/Drv/Drv.csproj") || a.Contains("src/Tool/Tool.csproj"));
    }

    [Fact]
    public void Setup_ANonFolderIsRefused()
    {
        var output = new StringWriter();

        Assert.Equal(1, RepoSetup.Setup(Path.Combine(_root, "missing"), output, new Answers()));
        Assert.Contains("refused", output.ToString());
    }

    [Fact]
    public void LocalCommands_RepoScanPrintsJson_AndUnknownVerbsAreLeftToTheBroker()
    {
        var output = new StringWriter();

        Assert.True(ElevLocalCommands.TryRun(["--repo-scan", _root], output, new Answers(), out var exit));
        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal(2, doc.RootElement.GetProperty("pendingProjects").GetArrayLength());

        Assert.False(ElevLocalCommands.TryRun([_root, "script.ps1"], output, new Answers(), out _));
        Assert.False(ElevLocalCommands.TryRun(["--verify", _root], output, new Answers(), out _));
    }

    [Fact]
    public void LocalCommands_MissingArgumentIsUsage()
    {
        var output = new StringWriter();

        Assert.True(ElevLocalCommands.TryRun(["repo-setup"], output, new Answers(), out var exit));
        Assert.Equal(2, exit);
        Assert.Contains("usage", output.ToString());
    }
}
