using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Bridge;
using AiDevHelpers.Helpers.SourceControlHelper;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Git status and diff run at once. Writes are two-step: git_plan shows the command, git_confirm runs it.</summary>
[McpServerToolType]
internal sealed class SourceControlTools
{
    private static readonly SourceControlService Service = new(new ProcessGitRunner());
    private static readonly GitHubService GitHub = new(new ProcessGhRunner());

    [McpServerTool]
    [Description("Branch, upstream, ahead and behind counts, and changed paths for a git repo folder.")]
    public static async Task<string> GitStatus(
        [Description("Repo folder, e.g. the folder that holds the .sln")] string repoPath,
        CancellationToken ct = default)
    {
        // Prefer the open VS Code window's git view when it has this folder; otherwise run git.exe.
        var viaVsCode = await TryVsCodeStatusAsync(repoPath, ct);
        if (viaVsCode is not null)
        {
            return viaVsCode;
        }

        var status = await Service.StatusAsync(repoPath, ct);
        return Json(new
        {
            ok = status.Error is null,
            branch = status.Branch,
            upstream = status.Upstream,
            ahead = status.Ahead,
            behind = status.Behind,
            changes = status.Changes.Select(c => new { path = c.Path, index = c.Index.ToString(), worktree = c.WorkTree.ToString() }),
            error = status.Error,
        });
    }

    // Returns the VS Code reply for this folder, or null when no VS Code instance has it open.
    private static async Task<string?> TryVsCodeStatusAsync(string repoPath, CancellationToken ct)
    {
        var full = Path.GetFullPath(repoPath);
        var open = VsInstanceRegistry.ListRunning().Any(i =>
            string.Equals(i.Ide, "VsCode", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(i.SolutionPath)
            && string.Equals(Path.GetFullPath(i.SolutionPath), full, StringComparison.OrdinalIgnoreCase));
        if (!open)
        {
            return null;
        }

        return await IdeInstanceRouter.CallAsync("gitStatus", new { repoPath = full }, full, ct, ide: "VsCode");
    }

    [McpServerTool]
    [Description("Diff of the working tree (or the staged changes when staged is true), optionally for one path.")]
    public static async Task<string> GitDiff(
        [Description("Repo folder")] string repoPath,
        [Description("true for staged changes, false for unstaged")] bool staged = false,
        [Description("Optional: one path to diff")] string? path = null)
    {
        var result = await Service.DiffAsync(repoPath, staged, path);
        return Json(new { ok = result.Ok, output = result.Output });
    }

    [McpServerTool]
    [Description("Plan a git write and return its exact command and a token. Nothing runs until git_confirm. " +
                 "action: stage, commit, pull, push, create_branch, delete_branch, switch, stash_push, stash_pop, stash_apply.")]
    public static string GitPlan(
        [Description("Repo folder")] string repoPath,
        [Description("One of the write actions above")] string action,
        [Description("Paths, for stage")] string[]? paths = null,
        [Description("Commit message, for commit")] string? message = null,
        [Description("Branch name, for push, create_branch, delete_branch and switch")] string? branch = null)
    {
        try
        {
            var pending = Service.Plan(repoPath, action, paths, message, branch);
            return Json(new { ok = true, token = pending.Token, action = pending.Action, command = pending.Description });
        }
        catch (ArgumentException ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [Description("Run a write planned by git_plan, once. The token is single use.")]
    public static async Task<string> GitConfirm(
        [Description("Token returned by git_plan")] string token)
    {
        var result = await Service.ConfirmAsync(token);
        return Json(new { ok = result.Ok, exitCode = result.ExitCode, output = result.Output });
    }

    [McpServerTool]
    [Description("Status of each CI check of a pull request, read through the GitHub CLI (gh). Read only. " +
                 "bucket is pass, fail, pending, skipping or cancel. Use it instead of waiting in a loop.")]
    public static async Task<string> PrChecks(
        [Description("Repo folder")] string repoPath,
        [Description("Pull request number")] int pr,
        CancellationToken ct = default)
    {
        var result = await GitHub.ChecksAsync(repoPath, pr, ct);
        return Json(new
        {
            ok = result.Error is null,
            count = result.Checks.Count,
            checks = result.Checks.Select(c => new { name = c.Name, state = c.State, bucket = c.Bucket, link = c.Link }),
            error = result.Error,
        });
    }

    [McpServerTool]
    [Description("The first failing CI job of a pull request and the error lines of its log, through gh. Read only. " +
                 "When no line looks like an error, the end of the log is returned. maxLines caps the lines.")]
    public static async Task<string> CiFailure(
        [Description("Repo folder")] string repoPath,
        [Description("Pull request number")] int pr,
        [Description("Most log lines to return")] int maxLines = 40,
        CancellationToken ct = default)
    {
        var failure = await GitHub.CiFailureAsync(repoPath, pr, maxLines, ct);
        return Json(new { ok = failure.Error is null, job = failure.Job, runId = failure.RunId, lines = failure.Lines, error = failure.Error });
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
