using Xunit;

namespace AiDevHelpers.Helpers.SourceControlHelper.Tests;

public sealed class LivePolicyTests
{
    private sealed class Gh(Func<string, GitResult> answer) : IGhRunner
    {
        public List<string> Calls { get; } = [];

        public Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            Calls.Add(string.Join(' ', arguments));
            return Task.FromResult(answer(arguments[1]));
        }
    }

    private const string Protection = """{"url":"https://api/x","required_status_checks":{"contexts":["build"],"checks":[{"context":"lint","app_id":1}]}}""";
    private const string NotProtected = """{"message":"Branch not protected","status":"404"}""";
    private const string Ruleset = """[{"type":"pull_request"},{"type":"required_status_checks","parameters":{"required_status_checks":[{"context":"test"}]}}]""";

    [Fact]
    public async Task ProtectionAndRulesets_BothCount_AndTheirChecksAreCollected()
    {
        var gh = new Gh(path => path.Contains("/protection") ? new GitResult(true, 0, Protection) : new GitResult(true, 0, Ruleset));

        var live = await new GitHubService(gh).LivePolicyAsync(@"C:\r", ["main", "feat/*"]);

        Assert.True(live.Checked);
        Assert.Equal(["main"], live.ProtectedBranches);
        Assert.Equal(["build", "lint", "test"], live.RequiredChecks.Order().ToList());
        Assert.Equal(2, gh.Calls.Count);
        Assert.All(gh.Calls, c => Assert.StartsWith("api repos/{owner}/{repo}/", c));
    }

    [Fact]
    public async Task ANotProtectedBranch_IsCheckedAndNotProtected()
    {
        var gh = new Gh(path => path.Contains("/protection") ? new GitResult(false, 1, NotProtected) : new GitResult(true, 0, "[]"));

        var live = await new GitHubService(gh).LivePolicyAsync(@"C:\r", ["feat/x"]);

        Assert.True(live.Checked);
        Assert.Empty(live.ProtectedBranches);
        Assert.Null(live.Error);
    }

    [Fact]
    public async Task WhenGhCannotAnswer_ItIsNotCheckedAndNeverReportedAsUnprotected()
    {
        var gh = new Gh(_ => new GitResult(false, -1, "gh is not installed"));

        var live = await new GitHubService(gh).LivePolicyAsync(@"C:\r", ["main"]);

        Assert.False(live.Checked);
        Assert.Empty(live.ProtectedBranches);
        Assert.Contains("not installed", live.Error);
    }

    [Fact]
    public async Task ABranchNameWithASlash_IsEscaped()
    {
        var gh = new Gh(_ => new GitResult(true, 0, "[]"));

        await new GitHubService(gh).LivePolicyAsync(@"C:\r", ["release/1.0"]);

        Assert.Contains(gh.Calls, c => c.Contains("release%2F1.0"));
    }
}
