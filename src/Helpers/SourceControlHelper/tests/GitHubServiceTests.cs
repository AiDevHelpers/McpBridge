using AiDevHelpers.Helpers.SourceControlHelper;
using Xunit;

namespace AiDevHelpers.Helpers.SourceControlHelper.Tests;

public sealed class GitHubServiceTests
{
    private const string ChecksJson =
        "[{\"name\":\"build-and-test\",\"state\":\"FAILURE\",\"bucket\":\"fail\",\"link\":\"https://github.com/o/r/actions/runs/123/job/456\",\"workflow\":\"CI\"}," +
        "{\"name\":\"vscode-extension\",\"state\":\"SUCCESS\",\"bucket\":\"pass\",\"link\":\"https://github.com/o/r/actions/runs/123/job/457\",\"workflow\":\"CI\"}]";

    private sealed class FakeGh(Func<IReadOnlyList<string>, GitResult> handler) : IGhRunner
    {
        public List<string> Calls { get; } = [];

        public Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            Calls.Add(string.Join(" ", arguments));
            return Task.FromResult(handler(arguments));
        }
    }

    [Fact]
    public async Task ChecksParsesEachJob()
    {
        var gh = new FakeGh(_ => new GitResult(true, 0, ChecksJson));

        var result = await new GitHubService(gh).ChecksAsync("repo", 7);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Checks.Count);
        Assert.Equal(new CheckStatus("build-and-test", "FAILURE", "fail", "https://github.com/o/r/actions/runs/123/job/456"), result.Checks[0]);
        Assert.Equal("pr checks 7 --json name,state,bucket,link", Assert.Single(gh.Calls));
    }

    [Fact]
    public async Task ChecksStillParsesWhenGhExitsNonZeroForFailingOrPendingChecks()
    {
        var gh = new FakeGh(_ => new GitResult(false, 8, ChecksJson));

        var result = await new GitHubService(gh).ChecksAsync("repo", 7);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Checks.Count);
    }

    [Fact]
    public async Task ChecksReportsGhOutputWhenThereIsNoJson()
    {
        var gh = new FakeGh(_ => new GitResult(false, 1, "no pull requests found"));

        var result = await new GitHubService(gh).ChecksAsync("repo", 7);

        Assert.Empty(result.Checks);
        Assert.Equal("no pull requests found", result.Error);
    }

    [Fact]
    public async Task CiFailureReadsTheFailedLogOfTheFailingRun()
    {
        var log = "build\tStep\tline one\nbuild\tStep\terror CS1002: ; expected\nbuild\tStep\tline three\nbuild\tStep\t##[error]Process completed with exit code 1.";
        var gh = new FakeGh(args => args[0] == "pr" ? new GitResult(false, 1, ChecksJson) : new GitResult(true, 0, log));

        var failure = await new GitHubService(gh).CiFailureAsync("repo", 7);

        Assert.Null(failure.Error);
        Assert.Equal("build-and-test", failure.Job);
        Assert.Equal("123", failure.RunId);
        Assert.Equal(["build\tStep\terror CS1002: ; expected", "build\tStep\t##[error]Process completed with exit code 1."], failure.Lines);
        Assert.Equal("run view 123 --log-failed", gh.Calls[1]);
    }

    [Fact]
    public async Task CiFailureFallsBackToTheEndOfTheLogWhenNoLineLooksLikeAnError()
    {
        var log = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line {i}"));
        var gh = new FakeGh(args => args[0] == "pr" ? new GitResult(false, 1, ChecksJson) : new GitResult(true, 0, log));

        var failure = await new GitHubService(gh).CiFailureAsync("repo", 7, maxLines: 3);

        Assert.Equal(["line 8", "line 9", "line 10"], failure.Lines);
    }

    [Fact]
    public async Task CiFailureCapsTheErrorLines()
    {
        var log = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"error E{i}"));
        var gh = new FakeGh(args => args[0] == "pr" ? new GitResult(false, 1, ChecksJson) : new GitResult(true, 0, log));

        var failure = await new GitHubService(gh).CiFailureAsync("repo", 7, maxLines: 4);

        Assert.Equal(4, failure.Lines.Count);
        Assert.Equal("error E1", failure.Lines[0]);
    }

    [Fact]
    public async Task CiFailureSaysSoWhenNothingFailed()
    {
        var passing = "[{\"name\":\"a\",\"state\":\"SUCCESS\",\"bucket\":\"pass\",\"link\":\"https://github.com/o/r/actions/runs/9/job/1\"}]";
        var gh = new FakeGh(_ => new GitResult(true, 0, passing));

        var failure = await new GitHubService(gh).CiFailureAsync("repo", 7);

        Assert.Null(failure.Job);
        Assert.False(string.IsNullOrWhiteSpace(failure.Error));
        Assert.Single(gh.Calls);
    }

    [Fact]
    public async Task CiFailureReportsAFailingJobWithoutARunLink()
    {
        var odd = "[{\"name\":\"external\",\"state\":\"FAILURE\",\"bucket\":\"fail\",\"link\":\"https://example.com/status\"}]";
        var gh = new FakeGh(_ => new GitResult(false, 1, odd));

        var failure = await new GitHubService(gh).CiFailureAsync("repo", 7);

        Assert.Equal("external", failure.Job);
        Assert.Null(failure.RunId);
        Assert.False(string.IsNullOrWhiteSpace(failure.Error));
    }
}
