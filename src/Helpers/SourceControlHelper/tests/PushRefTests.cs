using AiDevHelpers.Common;
using Xunit;

namespace AiDevHelpers.Helpers.SourceControlHelper.Tests;

/// <summary>push with a destination ref (pushRef): argv, validation and the rules check on the destination.</summary>
public sealed class PushRefTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "push-ref-" + Guid.NewGuid().ToString("N"));

    public PushRefTests() => Directory.CreateDirectory(Path.Combine(_root, ".aidevhelpers"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Runner : IGitRunner
    {
        public List<string> Calls { get; } = [];

        public Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            Calls.Add(string.Join(' ', arguments));
            return Task.FromResult(arguments is ["rev-parse", "--abbrev-ref", "HEAD"] ? new GitResult(true, 0, "feat/x\n") : new GitResult(true, 0, "aaa\n"));
        }
    }

    private sealed class Source(string root, EffectiveRules rules) : IRepoRulesSource
    {
        public (string Root, EffectiveRules Rules)? For(string folder) => (root, rules);
    }

    [Fact]
    public void DestinationOnlyPushesHeadToThatBranch()
    {
        Assert.Equal(["push", "origin", "HEAD:refs/heads/wip"], SourceControlService.BuildArguments("push", null, null, null, "wip"));
    }

    [Fact]
    public void SourceColonDestinationPushesThatSource()
    {
        Assert.Equal(["push", "origin", "feat/x:refs/heads/wip"], SourceControlService.BuildArguments("push", null, null, null, "feat/x:wip"));
    }

    [Fact]
    public void PushRefAndBranchTogetherAreRefused()
    {
        Assert.Throws<ArgumentException>(() => SourceControlService.BuildArguments("push", null, null, "feat/x", "wip"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-force")]
    [InlineData("a b")]
    [InlineData("a:b:c")]
    [InlineData("x..y")]
    public void InvalidDestinationIsRefusedAtPlanTime(string pushRef)
    {
        Assert.Throws<ArgumentException>(() => SourceControlService.BuildArguments("push", null, null, null, pushRef));
    }

    [Fact]
    public async Task ProtectedDestinationIsRefusedByRules()
    {
        var rules = new EffectiveRules(RepoRules.Empty with { ProtectedBranches = ["wip"] }, null);
        var runner = new Runner();
        var service = new SourceControlService(runner, new Source(_root, rules));

        await Assert.ThrowsAsync<RuleRefusedException>(() => service.PlanAsync(_root, "push", pushRef: "wip"));
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("push", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlannedPushRefCarriesDestinationAsItsBranch()
    {
        var service = new SourceControlService(new Runner());

        var planned = await service.PlanAsync(_root, "push", pushRef: "wip");

        Assert.Equal("wip", planned.Branch);
        Assert.Equal("git push origin HEAD:refs/heads/wip", planned.Description);
    }
}

