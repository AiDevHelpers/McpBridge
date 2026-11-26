using System;
using System.IO;

namespace AiDevHelpers.Bridge;

/// <summary>
/// Reads the git branch and worktree folder of a solution from its files, without running git. A worktree or submodule has
/// a <c>.git</c> file that points at its git folder; a normal clone has a <c>.git</c> folder. A detached HEAD has no branch.
/// </summary>
public static class GitWorktree
{
    /// <summary>The worktree folder (the one holding <c>.git</c>) above a solution path, or null when the solution is not in a git checkout.</summary>
    public static string? RootFor(string solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            return null;
        }

        for (var dir = Path.GetDirectoryName(Path.GetFullPath(solutionPath)); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var git = Path.Combine(dir, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return dir;
            }
        }

        return null;
    }

    /// <summary>The branch checked out in the worktree that holds the solution, or null (not a checkout, or a detached HEAD).</summary>
    public static string? BranchFor(string solutionPath)
    {
        var root = RootFor(solutionPath);
        if (root is null)
        {
            return null;
        }

        var gitDir = GitDirOf(root);
        var headPath = gitDir is null ? null : Path.Combine(gitDir, "HEAD");
        if (headPath is null || !File.Exists(headPath))
        {
            return null;
        }

        const string prefix = "ref: refs/heads/";
        var head = File.ReadAllText(headPath).Trim();
        return head.StartsWith(prefix, StringComparison.Ordinal) ? head.Substring(prefix.Length) : null;
    }

    // A worktree's .git file says "gitdir: <path>"; a relative path is relative to the worktree folder.
    private static string? GitDirOf(string root)
    {
        var dotGit = Path.Combine(root, ".git");
        if (Directory.Exists(dotGit))
        {
            return dotGit;
        }

        var line = File.ReadAllText(dotGit).Trim();
        const string prefix = "gitdir:";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var target = line.Substring(prefix.Length).Trim();
        return Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(root, target));
    }
}
