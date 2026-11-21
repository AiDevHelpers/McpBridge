using AiDevHelpers.Helpers.SourceControlHelper;
using Xunit;

namespace AiDevHelpers.Helpers.SourceControlHelper.Tests;

public sealed class GitStatusParserTests
{
    [Fact]
    public void ParsesBranchTrackingAndChanges()
    {
        var status = GitStatusParser.Parse(
            "## main...origin/main [ahead 2, behind 1]\n" +
            "M  src/a.cs\n" +
            " M src/b.cs\n" +
            "?? new.txt\n" +
            "R  old.cs -> renamed.cs\n");

        Assert.Equal("main", status.Branch);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(2, status.Ahead);
        Assert.Equal(1, status.Behind);
        Assert.Equal(4, status.Changes.Count);
        Assert.Equal(new GitChange("src/a.cs", 'M', ' '), status.Changes[0]);
        Assert.Equal(new GitChange("src/b.cs", ' ', 'M'), status.Changes[1]);
        Assert.Equal(new GitChange("new.txt", '?', '?'), status.Changes[2]);
        Assert.Equal("renamed.cs", status.Changes[3].Path);
        Assert.Null(status.Error);
    }

    [Fact]
    public void ParsesRepoWithNoCommitsAndNoUpstream()
    {
        var status = GitStatusParser.Parse("## No commits yet on main\n");

        Assert.Equal("main", status.Branch);
        Assert.Null(status.Upstream);
        Assert.Empty(status.Changes);
    }

    [Fact]
    public void ParsesDetachedHead()
    {
        var status = GitStatusParser.Parse("## HEAD (no branch)\n");

        Assert.Equal("HEAD (no branch)", status.Branch);
    }
}

public sealed class BuildArgumentsTests
{
    [Fact]
    public void StageAddsPathsAfterDoubleDash()
    {
        var args = SourceControlService.BuildArguments("stage", ["a.cs", "b dir/c.cs"], null, null);

        Assert.Equal(["add", "--", "a.cs", "b dir/c.cs"], args);
    }

    [Fact]
    public void StageRequiresAPath()
    {
        Assert.Throws<ArgumentException>(() => SourceControlService.BuildArguments("stage", [], null, null));
    }

    [Fact]
    public void CommitNeedsAMessage()
    {
        Assert.Throws<ArgumentException>(() => SourceControlService.BuildArguments("commit", null, "  ", null));
    }

    [Fact]
    public void CreateBranchSwitchesToNewBranch()
    {
        Assert.Equal(["switch", "-c", "feature/x"], SourceControlService.BuildArguments("create_branch", null, null, "feature/x"));
    }
}

/// <summary>Real git against a scratch repo: reads, planned writes and the approval gate.</summary>
public sealed class SourceControlServiceTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "sc-tests", Guid.NewGuid().ToString("N"));
    private readonly SourceControlService _service = new(new ProcessGitRunner());

    public SourceControlServiceTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "test@example.invalid");
        Git("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "one\n");
        Git("add", "a.txt");
        Git("commit", "-q", "-m", "first");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_repo))
        {
            return;
        }

        // Git writes read-only object files, so clear the attribute before deleting.
        foreach (var file in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_repo, recursive: true);
    }

    private void Git(params string[] args)
    {
        var result = new ProcessGitRunner().Run(_repo, args);
        Assert.True(result.Ok, result.Output);
    }

    [Fact]
    public void StatusReportsAModifiedFile()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "two\n");

        var status = _service.Status(_repo);

        Assert.Equal("main", status.Branch);
        var change = Assert.Single(status.Changes);
        Assert.Equal("a.txt", change.Path);
        Assert.Equal(' ', change.Index);
        Assert.Equal('M', change.WorkTree);
    }

    [Fact]
    public void DiffShowsTheChange()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "two\n");

        var diff = _service.Diff(_repo, staged: false, path: "a.txt");

        Assert.True(diff.Ok);
        Assert.Contains("+two", diff.Output);
    }

    [Fact]
    public void PlannedStageAndCommitRunOnlyWhenConfirmed()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "two\n");

        var stage = _service.Plan(_repo, "stage", paths: ["a.txt"]);
        Assert.Equal('M', Assert.Single(_service.Status(_repo).Changes).WorkTree);
        Assert.True(_service.Confirm(stage.Token).Ok);

        var commit = _service.Plan(_repo, "commit", message: "second");
        Assert.True(_service.Confirm(commit.Token).Ok);
        Assert.Empty(_service.Status(_repo).Changes);
    }

    [Fact]
    public void PlanRunsNothingUntilConfirmed()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "two\n");

        _service.Plan(_repo, "stage", paths: ["a.txt"]);

        Assert.Equal('M', Assert.Single(_service.Status(_repo).Changes).WorkTree);
    }

    [Fact]
    public void ConfirmedTokenCannotBeUsedTwice()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "two\n");
        var stage = _service.Plan(_repo, "stage", paths: ["a.txt"]);
        _service.Confirm(stage.Token);

        var again = _service.Confirm(stage.Token);

        Assert.False(again.Ok);
        Assert.Contains("Unknown or already used token", again.Output);
    }

    [Fact]
    public void UnknownActionIsRefused()
    {
        Assert.Throws<ArgumentException>(() => _service.Plan(_repo, "rebase_hard"));
    }
}
