using AiDevHelpers.Common;
using Xunit;

namespace AiDevHelpers.Helpers.SourceControlHelper.Tests;

/// <summary>worktree_add, worktree_remove and delete_branch with mergedInto: argv, path checks, the ancestor check and the VS Code route.</summary>
public sealed class WorktreeTests : IDisposable
{
    private static readonly string Repo = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wt-repo"));
    private static readonly string Other = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wt-other"));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wt-" + Guid.NewGuid().ToString("N"));

    public WorktreeTests() => Directory.CreateDirectory(Path.Combine(_root, ".aidevhelpers"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Runner(bool ancestor = true, string worktrees = "") : IGitRunner
    {
        public bool Ancestor { get; set; } = ancestor;
        public List<string> Calls { get; } = [];

        public Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            var line = string.Join(' ', arguments);
            Calls.Add(line);
            GitResult result = arguments[0] switch
            {
                "worktree" when arguments[1] == "list" => new GitResult(true, 0, worktrees),
                "merge-base" => new GitResult(Ancestor, Ancestor ? 0 : 1, string.Empty),
                _ => new GitResult(true, 0, string.Empty),
            };
            return Task.FromResult(result);
        }
    }

    private sealed class Router : IVsCodeWriteRouter
    {
        public int Calls { get; private set; }

        public Task<GitResult?> TryRunAsync(string repoPath, string vsCodeAction, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<GitResult?>(new GitResult(true, 0, "{}"));
        }
    }

    private static string Listing(params string[] paths) => string.Join('\n', paths.Select(p => "worktree " + p)) + "\n";

    [Fact]
    public void WorktreeAddCreatesANewBranchAtThePath()
    {
        Assert.Equal(["worktree", "add", "-b", "feat/y", "../wt"], SourceControlService.BuildArguments("worktree_add", null, null, "feat/y", null, "../wt"));
    }

    [Fact]
    public void WorktreeAddNeedsAPathAndABranch()
    {
        Assert.Throws<ArgumentException>(() => SourceControlService.BuildArguments("worktree_add", null, null, "feat/y", null, null));
        Assert.Throws<ArgumentException>(() => SourceControlService.BuildArguments("worktree_add", null, null, null, null, "../wt"));
    }

    [Fact]
    public void ForceDeleteWithMergedIntoUsesCapitalD()
    {
        Assert.Equal(["branch", "-D", "feat/x"], SourceControlService.BuildArguments("delete_branch", null, null, "feat/x", null, null, "wip"));
    }

    [Fact]
    public async Task RemoveOfTheMainWorktreeIsRefused()
    {
        var runner = new Runner(worktrees: Listing(Repo, Other));
        var service = new SourceControlService(runner);

        await Assert.ThrowsAsync<ArgumentException>(() => service.PlanAsync(Repo, "worktree_remove", path: Repo));
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("worktree remove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoveOfTheCurrentWorktreeIsRefused()
    {
        var runner = new Runner(worktrees: Listing(Other, Repo));
        var service = new SourceControlService(runner);

        await Assert.ThrowsAsync<ArgumentException>(() => service.PlanAsync(Repo, "worktree_remove", path: Repo));
    }

    [Fact]
    public async Task RemoveOfAnUnlistedPathIsRefused()
    {
        var service = new SourceControlService(new Runner(worktrees: Listing(Repo)));

        await Assert.ThrowsAsync<ArgumentException>(() => service.PlanAsync(Repo, "worktree_remove", path: Other));
    }

    [Fact]
    public async Task RemoveOfAListedLinkedWorktreeIsPlanned()
    {
        var service = new SourceControlService(new Runner(worktrees: Listing(Repo, Other)));

        var planned = await service.PlanAsync(Repo, "worktree_remove", path: Other);

        Assert.Equal(["worktree", "remove", Other], planned.Arguments);
    }

    [Fact]
    public async Task ForceDeleteIsRefusedWhenTheBranchIsNotAnAncestor()
    {
        var service = new SourceControlService(new Runner(ancestor: false));

        await Assert.ThrowsAsync<ArgumentException>(() => service.PlanAsync(Repo, "delete_branch", branch: "feat/x", mergedInto: "wip"));
    }

    [Fact]
    public async Task ForceDeleteIsRecheckedAtConfirm()
    {
        var runner = new Runner(ancestor: true);
        var service = new SourceControlService(runner);
        var planned = await service.PlanAsync(Repo, "delete_branch", branch: "feat/x", mergedInto: "wip");

        // The branch moved to a commit that is no longer an ancestor of wip, between plan and confirm.
        runner.Ancestor = false;
        var confirm = await service.ConfirmAsync(planned.Token);

        Assert.False(confirm.Ok);
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("branch -D", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForceDeleteStaysOnGitAndNeverReachesVsCode()
    {
        var router = new Router();
        var service = new SourceControlService(new Runner(), null, router);
        var planned = await service.PlanAsync(Repo, "delete_branch", branch: "feat/x", mergedInto: "wip");

        _ = await service.ConfirmAsync(planned.Token);

        Assert.Equal(0, router.Calls);
    }
}

