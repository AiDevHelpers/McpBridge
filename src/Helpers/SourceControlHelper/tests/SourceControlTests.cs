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
public sealed class SourceControlServiceTests : IAsyncLifetime, IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "sc-tests", Guid.NewGuid().ToString("N"));
    private readonly SourceControlService _service = new(new ProcessGitRunner());

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_repo);
        await GitAsync("init", "-q", "-b", "main");
        await GitAsync("config", "user.email", "test@example.invalid");
        await GitAsync("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "one\n");
        await GitAsync("add", "a.txt");
        await GitAsync("commit", "-q", "-m", "first");
    }

    public Task DisposeAsync() => Task.CompletedTask;

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

    private async Task GitAsync(params string[] args)
    {
        var result = await new ProcessGitRunner().RunAsync(_repo, args);
        Assert.True(result.Ok, result.Output);
    }

    [Fact]
    public async Task StatusReportsAModifiedFile()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "two\n");

        var status = await _service.StatusAsync(_repo);

        Assert.Equal("main", status.Branch);
        var change = Assert.Single(status.Changes);
        Assert.Equal("a.txt", change.Path);
        Assert.Equal(' ', change.Index);
        Assert.Equal('M', change.WorkTree);
    }

    [Fact]
    public async Task DiffShowsTheChange()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "two\n");

        var diff = await _service.DiffAsync(_repo, staged: false, path: "a.txt");

        Assert.True(diff.Ok);
        Assert.Contains("+two", diff.Output);
    }

    [Fact]
    public async Task PlannedStageAndCommitRunOnlyWhenConfirmed()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "two\n");

        var stage = _service.Plan(_repo, "stage", paths: ["a.txt"]);
        Assert.Equal('M', Assert.Single((await _service.StatusAsync(_repo)).Changes).WorkTree);
        Assert.True((await _service.ConfirmAsync(stage.Token)).Ok);

        var commit = _service.Plan(_repo, "commit", message: "second");
        Assert.True((await _service.ConfirmAsync(commit.Token)).Ok);
        Assert.Empty((await _service.StatusAsync(_repo)).Changes);
    }

    [Fact]
    public async Task PlanRunsNothingUntilConfirmed()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "two\n");

        _service.Plan(_repo, "stage", paths: ["a.txt"]);

        Assert.Equal('M', Assert.Single((await _service.StatusAsync(_repo)).Changes).WorkTree);
    }

    [Fact]
    public async Task ConfirmedTokenCannotBeUsedTwice()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "two\n");
        var stage = _service.Plan(_repo, "stage", paths: ["a.txt"]);
        await _service.ConfirmAsync(stage.Token);

        var again = await _service.ConfirmAsync(stage.Token);

        Assert.False(again.Ok);
        Assert.Contains("Unknown or already used token", again.Output);
    }

    [Fact]
    public async Task SequenceRunsEveryStepUnderOneToken()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "two\n");

        var sequence = await _service.PlanSequenceAsync(_repo, ["stage", "commit"], paths: ["a.txt"], message: "second");

        Assert.Equal(2, sequence.Steps.Count);
        Assert.Equal('M', Assert.Single((await _service.StatusAsync(_repo)).Changes).WorkTree);
        var result = await _service.ConfirmAsync(sequence.Token);
        Assert.True(result.Ok, result.Output);
        Assert.Contains("$ git add -- a.txt", result.Output);
        Assert.Contains("$ git commit -m second", result.Output);
        Assert.Empty((await _service.StatusAsync(_repo)).Changes);
    }

    [Fact]
    public async Task SequenceStopsAtTheFirstFailure()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "two\n");

        var sequence = await _service.PlanSequenceAsync(_repo, ["stage", "commit"], paths: ["missing.txt"], message: "second");
        var result = await _service.ConfirmAsync(sequence.Token);

        Assert.False(result.Ok);
        Assert.Contains("$ git add -- missing.txt", result.Output);
        Assert.DoesNotContain("$ git commit", result.Output);
        Assert.Equal('M', Assert.Single((await _service.StatusAsync(_repo)).Changes).WorkTree);
    }

    [Fact]
    public async Task SequenceTokenCannotBeUsedTwice()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "two\n");
        var sequence = await _service.PlanSequenceAsync(_repo, ["stage"], paths: ["a.txt"]);
        await _service.ConfirmAsync(sequence.Token);

        var again = await _service.ConfirmAsync(sequence.Token);

        Assert.False(again.Ok);
        Assert.Contains("Unknown or already used token", again.Output);
    }

    [Fact]
    public async Task EmptySequenceIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.PlanSequenceAsync(_repo, []));
    }

    [Fact]
    public void UnknownActionIsRefused()
    {
        Assert.Throws<ArgumentException>(() => _service.Plan(_repo, "rebase_hard"));
    }
}

/// <summary>A git call that fails without printing anything must still say why.</summary>
public sealed class StatusFailureTests
{
    private sealed class FailingRunner(GitResult result) : IGitRunner
    {
        public Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    [Fact]
    public async Task SilentFailureReportsTheExitCode()
    {
        var service = new SourceControlService(new FailingRunner(new GitResult(false, 128, "")));

        var status = await service.StatusAsync("anywhere");

        Assert.False(string.IsNullOrWhiteSpace(status.Error));
        Assert.Contains("128", status.Error);
    }

    [Fact]
    public async Task FailureWithOutputKeepsTheOutput()
    {
        var service = new SourceControlService(new FailingRunner(new GitResult(false, 128, "fatal: not a git repository")));

        var status = await service.StatusAsync("anywhere");

        Assert.Equal("fatal: not a git repository", status.Error);
    }
}
