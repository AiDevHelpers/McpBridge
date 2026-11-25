using AiDevHelpers.Common;
using Xunit;

namespace AiDevHelpers.Helpers.SourceControlHelper.Tests;

public sealed class RuleEnforcementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rule-enf-" + Guid.NewGuid().ToString("N"));

    public RuleEnforcementTests() => Directory.CreateDirectory(Path.Combine(_root, ".aidevhelpers"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Runner(string branch) : IGitRunner
    {
        public List<string> Calls { get; } = [];

        public Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            Calls.Add(string.Join(' ', arguments));
            return Task.FromResult(arguments is ["rev-parse", ..] ? new GitResult(branch.Length > 0, branch.Length > 0 ? 0 : 128, branch + "\n") : new GitResult(true, 0, string.Empty));
        }
    }

    private sealed class Source(string root, EffectiveRules? rules) : IRepoRulesSource
    {
        public EffectiveRules? Rules { get; set; } = rules;

        public (string Root, EffectiveRules Rules)? For(string folder) => Rules is null ? null : (root, Rules);
    }

    private static EffectiveRules Rules(string[]? protectedBranches = null, string? pattern = null) =>
        new(RepoRules.Empty with { ProtectedBranches = protectedBranches ?? [], BranchNamePattern = pattern }, null);

    private SourceControlService Service(Runner runner, EffectiveRules? rules) => new(runner, new Source(_root, rules));

    [Fact]
    public async Task WithNoRules_EveryWriteIsPlanned()
    {
        var runner = new Runner("main");

        Assert.NotNull(await Service(runner, null).PlanAsync(_root, "push"));
        Assert.NotNull(await Service(runner, Rules()).PlanAsync(_root, "commit", message: "m"));
    }

    [Fact]
    public async Task ACommitOnAProtectedBranch_IsRefused_WithNoToken()
    {
        var runner = new Runner("main");

        var ex = await Assert.ThrowsAsync<RuleRefusedException>(() => Service(runner, Rules(["main"])).PlanAsync(_root, "commit", message: "m"));

        Assert.Contains("protected", ex.Message);
        Assert.Contains("elev rules override", ex.Message);
        Assert.Contains("create_branch", ex.Message);
    }

    [Fact]
    public async Task ACommitOnAWorkBranch_IsPlanned()
    {
        var plan = await Service(new Runner("feat/x"), Rules(["main", "release/*"])).PlanAsync(_root, "commit", message: "m");

        Assert.Equal("commit", plan.Action);
    }

    [Theory]
    [InlineData("main", "main")]
    [InlineData("main", null)]
    [InlineData("feat/x", "release/1.0")]
    public async Task APushToAProtectedBranch_IsRefused_NamedOrBare(string current, string? target)
    {
        var service = Service(new Runner(current), Rules(["main", "release/*"]));

        await Assert.ThrowsAsync<RuleRefusedException>(() => service.PlanAsync(_root, "push", branch: target));
    }

    [Fact]
    public async Task APushOfAWorkBranch_IsPlanned_NamedOrBare()
    {
        var service = Service(new Runner("feat/x"), Rules(["main"]));

        Assert.NotNull(await service.PlanAsync(_root, "push", branch: "feat/x"));
        Assert.NotNull(await service.PlanAsync(_root, "push"));
    }

    [Fact]
    public async Task DeletingAProtectedBranch_AndABadBranchName_AreRefused()
    {
        var service = Service(new Runner("feat/x"), Rules(["main"], "^(feat|fix)/.+"));

        await Assert.ThrowsAsync<RuleRefusedException>(() => service.PlanAsync(_root, "delete_branch", branch: "main"));
        await Assert.ThrowsAsync<RuleRefusedException>(() => service.PlanAsync(_root, "create_branch", branch: "random-name"));
        Assert.NotNull(await service.PlanAsync(_root, "create_branch", branch: "feat/new"));
        Assert.NotNull(await service.PlanAsync(_root, "delete_branch", branch: "feat/old"));
    }

    [Fact]
    public async Task WhenGitCannotNameTheBranch_ACommitIsNotRefused()
    {
        Assert.NotNull(await Service(new Runner(string.Empty), Rules(["main"])).PlanAsync(_root, "commit", message: "m"));
    }

    [Fact]
    public async Task AGrantedOverride_AllowsTheNextMatchingPlanOnce_AndConfirmDoesNotRefuseIt()
    {
        var runner = new Runner("main");
        var service = Service(runner, Rules(["main"]));

        var refused = await Assert.ThrowsAsync<RuleRefusedException>(() => service.PlanAsync(_root, "commit", message: "m"));
        var id = System.Text.RegularExpressions.Regex.Match(refused.Message, "override \".*?\" (?<id>[0-9a-f]{8})").Groups["id"].Value;
        Assert.Equal(8, id.Length);
        Assert.True(RuleOverrides.Grant(_root, id, DateTime.UtcNow));

        var plan = await service.PlanAsync(_root, "commit", message: "m");
        var result = await service.ConfirmAsync(plan.Token);

        Assert.True(result.Ok);
        Assert.Contains("commit -m m", runner.Calls);
        await Assert.ThrowsAsync<RuleRefusedException>(() => service.PlanAsync(_root, "commit", message: "m"));
    }

    [Fact]
    public async Task ARuleAddedBetweenPlanAndConfirm_RefusesTheWrite_AndRunsNothing()
    {
        var runner = new Runner("feat/x");
        var source = new Source(_root, Rules(["main"]));
        var service = new SourceControlService(runner, source);
        var plan = await service.PlanAsync(_root, "push", branch: "feat/x");

        source.Rules = Rules(["main", "feat/*"]);
        var result = await service.ConfirmAsync(plan.Token);

        Assert.False(result.Ok);
        Assert.Contains("changed since", result.Output);
        Assert.DoesNotContain("push origin feat/x", runner.Calls);
    }

    [Fact]
    public void MissingChecks_AreTheRequiredNamesAPullRequestLacks_IgnoringCase()
    {
        Assert.Equal(["lint"], RuleGuard.MissingChecks(["Build", "lint"], ["build", "test"]));
        Assert.Empty(RuleGuard.MissingChecks([], ["build"]));
    }

    [Fact]
    public void MissingGateCommands_AreTheRequiredCommandsNoStepRuns()
    {
        var steps = new[] { "dotnet build  AiDevHelpers.slnx -c Release", "dotnet test AiDevHelpers.slnx" };

        Assert.Equal(["npm run lint"], RuleGuard.MissingGateCommands(["dotnet test", "npm run lint"], steps));
    }

    [Fact]
    public async Task WritesThatAreNotGuarded_NeverAskGitForTheBranch()
    {
        var runner = new Runner("main");

        await Service(runner, Rules(["main"])).PlanAsync(_root, "stage", paths: ["a.txt"]);

        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("rev-parse", StringComparison.Ordinal));
    }
}
