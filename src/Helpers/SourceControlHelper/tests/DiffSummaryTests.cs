using AiDevHelpers.Helpers.SourceControlHelper;
using Xunit;

namespace AiDevHelpers.Helpers.SourceControlHelper.Tests;

public sealed class DiffSummaryTests
{
    private sealed class FakeGit(GitResult numstat, GitResult raw) : IGitRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            Calls.Add(arguments);
            return Task.FromResult(arguments.Contains("--numstat") ? numstat : raw);
        }
    }

    private const string Numstat = "3\t1\tsrc/a.cs\n0\t2\tsrc/b.cs\n-\t-\tlogo.png\n";
    private const string Raw = "diff --git a/src/a.cs b/src/a.cs\n+added\n";

    [Fact]
    public void Numstat_parses_each_file_and_marks_binary_files()
    {
        var files = DiffStatParser.Parse(Numstat);

        Assert.Equal(3, files.Count);
        Assert.Equal(("src/a.cs", 3, 1, false), (files[0].Path, files[0].Added, files[0].Deleted, files[0].Binary));
        Assert.Equal("logo.png", files[2].Path);
        Assert.True(files[2].Binary);
    }

    [Fact]
    public async Task Summary_reports_stats_totals_and_the_raw_diff()
    {
        var git = new FakeGit(new GitResult(true, 0, Numstat), new GitResult(true, 0, Raw));
        var service = new SourceControlService(git);

        var summary = await service.SummarizeDiffAsync("repo");

        Assert.True(summary.Ok);
        Assert.Equal(3, summary.Files.Count);
        Assert.Equal(3, summary.Added);
        Assert.Equal(3, summary.Deleted);
        Assert.Equal(Raw, summary.RawDiff);
        Assert.Equal(Raw.Length, summary.RawChars);
    }

    [Fact]
    public async Task Staged_adds_cached_to_both_calls()
    {
        var git = new FakeGit(new GitResult(true, 0, Numstat), new GitResult(true, 0, Raw));

        await new SourceControlService(git).SummarizeDiffAsync("repo", staged: true);

        Assert.All(git.Calls, args => Assert.Contains("--cached", args));
    }

    [Fact]
    public async Task One_path_is_passed_after_a_separator()
    {
        var git = new FakeGit(new GitResult(true, 0, Numstat), new GitResult(true, 0, Raw));

        await new SourceControlService(git).SummarizeDiffAsync("repo", path: "src/a.cs");

        Assert.All(git.Calls, args => Assert.Equal(["--", "src/a.cs"], args.Skip(args.Count - 2)));
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("src/../../secret.txt")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("/etc/passwd")]
    public async Task A_path_outside_the_repo_is_refused_before_git_runs(string path)
    {
        var git = new FakeGit(new GitResult(true, 0, Numstat), new GitResult(true, 0, Raw));

        var summary = await new SourceControlService(git).SummarizeDiffAsync("repo", path: path);

        Assert.False(summary.Ok);
        Assert.False(string.IsNullOrWhiteSpace(summary.Error));
        Assert.Empty(git.Calls);
    }

    [Fact]
    public async Task A_failed_git_call_is_reported_with_its_output()
    {
        var git = new FakeGit(new GitResult(false, 128, "fatal: not a git repository"), new GitResult(false, 128, "fatal: not a git repository"));

        var summary = await new SourceControlService(git).SummarizeDiffAsync("repo");

        Assert.False(summary.Ok);
        Assert.Contains("not a git repository", summary.Error);
    }
}
