using AiDevHelpers.Common;
using Xunit;

namespace AiDevHelpers.Helpers.SourceControlHelper.Tests;

/// <summary>merge and merge_abort: argv, the protected-branch rule on the current branch, the MERGE_HEAD precondition and conflict output.</summary>
public sealed class MergeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "merge-" + Guid.NewGuid().ToString("N"));

    public MergeTests() => Directory.CreateDirectory(Path.Combine(_root, ".aidevhelpers"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Runner(string branch, bool merging, GitResult? mergeResult = null, string unmerged = "") : IGitRunner
    {
        public List<string> Calls { get; } = [];

        public Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            var line = string.Join(' ', arguments);
            Calls.Add(line);
            GitResult result = line switch
            {
                "rev-parse --abbrev-ref HEAD" => new GitResult(true, 0, branch + "\n"),
                "rev-parse --verify -q MERGE_HEAD" => merging ? new GitResult(true, 0, "abc\n") : new GitResult(false, 1, string.Empty),
                "diff --name-only --diff-filter=U" => new GitResult(true, 0, unmerged),
                _ when arguments[0] == "merge" && mergeResult is not null => mergeResult,
                _ => new GitResult(true, 0, string.Empty),
            };
            return Task.FromResult(result);
        }
    }

    private sealed class Source(string root, EffectiveRules rules) : IRepoRulesSource
    {
        public (string Root, EffectiveRules Rules)? For(string folder) => (root, rules);
    }

    [Fact]
    public void MergeUsesNoFastForwardAndNoEditor()
    {
        Assert.Equal(["merge", "--no-ff", "--no-edit", "origin/wip"], SourceControlService.BuildArguments("merge", null, null, "origin/wip"));
    }

    [Fact]
    public void MergeNeedsASource()
    {
        Assert.Throws<ArgumentException>(() => SourceControlService.BuildArguments("merge", null, null, null));
    }

    [Fact]
    public void MergeAbortArguments()
    {
        Assert.Equal(["merge", "--abort"], SourceControlService.BuildArguments("merge_abort", null, null, null));
    }

    [Fact]
    public async Task MergeIntoProtectedCurrentBranchIsRefused()
    {
        var rules = new EffectiveRules(RepoRules.Empty with { ProtectedBranches = ["main"] }, null);
        var runner = new Runner("main", merging: false);
        var service = new SourceControlService(runner, new Source(_root, rules));

        await Assert.ThrowsAsync<RuleRefusedException>(() => service.PlanAsync(_root, "merge", branch: "origin/wip"));
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("merge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MergeIntoUnprotectedBranchIsPlanned()
    {
        var rules = new EffectiveRules(RepoRules.Empty with { ProtectedBranches = ["main"] }, null);
        var service = new SourceControlService(new Runner("feat/x", merging: false), new Source(_root, rules));

        var planned = await service.PlanAsync(_root, "merge", branch: "origin/wip");

        Assert.Equal("git merge --no-ff --no-edit origin/wip", planned.Description);
    }

    [Fact]
    public async Task MergeAbortIsRefusedWhenNoMergeIsInProgress()
    {
        var service = new SourceControlService(new Runner("feat/x", merging: false));

        await Assert.ThrowsAsync<ArgumentException>(() => service.PlanAsync(_root, "merge_abort"));
    }

    [Fact]
    public async Task MergeAbortIsPlannedDuringAMerge()
    {
        var service = new SourceControlService(new Runner("feat/x", merging: true));

        var planned = await service.PlanAsync(_root, "merge_abort");

        Assert.Equal("git merge --abort", planned.Description);
    }

    [Fact]
    public async Task MergeAbortInsideASequenceIsRefused()
    {
        var service = new SourceControlService(new Runner("feat/x", merging: true));

        await Assert.ThrowsAsync<ArgumentException>(() => service.PlanSequenceAsync(_root, ["stage", "merge_abort"], paths: ["a.txt"]));
    }

    [Fact]
    public async Task ConflictedMergeReportsTheUnmergedPaths()
    {
        var runner = new Runner("feat/x", merging: false,
            mergeResult: new GitResult(false, 1, "CONFLICT (content): Merge conflict in a.txt\n"),
            unmerged: "a.txt\nb.txt\n");
        var service = new SourceControlService(runner);
        var planned = await service.PlanAsync(_root, "merge", branch: "origin/wip");

        var result = await service.ConfirmAsync(planned.Token);

        Assert.False(result.Ok);
        Assert.Contains("CONFLICT (content)", result.Output);
        Assert.Contains("a.txt", result.Output.Split('\n').Skip(1));
        Assert.Contains("b.txt", result.Output);
    }
}

