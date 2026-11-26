using System;
using System.IO;
using AiDevHelpers.Bridge;
using Xunit;

namespace AiDevHelpers.Bridge.Tests;

/// <summary>Reads the branch and worktree folder of a solution from its git files.</summary>
public sealed class GitWorktreeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aidevhelpers-git-worktree-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void CloneReadsBranchAndRoot()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/wip\n");
        Directory.CreateDirectory(Path.Combine(repo, "src"));

        var solution = Path.Combine(repo, "src", "App.sln");

        Assert.Equal("wip", GitWorktree.BranchFor(solution));
        Assert.Equal(Path.GetFullPath(repo), GitWorktree.RootFor(solution));
    }

    [Fact]
    public void WorktreeFollowsGitFileToItsBranch()
    {
        var gitDir = Path.Combine(_root, "main", ".git", "worktrees", "feat");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/feature/x\n");

        var worktree = Path.Combine(_root, "feat");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + gitDir + "\n");

        var solution = Path.Combine(worktree, "App.sln");

        Assert.Equal("feature/x", GitWorktree.BranchFor(solution));
        Assert.Equal(Path.GetFullPath(worktree), GitWorktree.RootFor(solution));
    }

    [Fact]
    public void DetachedHeadHasNoBranch()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "3a38386d0c0e4f0b1f2a3b4c5d6e7f8091a2b3c4\n");

        Assert.Null(GitWorktree.BranchFor(Path.Combine(repo, "App.sln")));
    }

    [Fact]
    public void NotACheckoutHasNoBranchOrRoot()
    {
        Directory.CreateDirectory(_root);
        var solution = Path.Combine(_root, "App.sln");

        Assert.Null(GitWorktree.BranchFor(solution));
        Assert.Null(GitWorktree.RootFor(solution));
    }
}
