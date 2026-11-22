using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Helpers.SourceControlHelper;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Git status and diff run at once. Writes are two-step: git_plan shows the command, git_confirm runs it.</summary>
[McpServerToolType]
internal sealed class SourceControlTools
{
    private static readonly SourceControlService Service = new(new ProcessGitRunner());

    [McpServerTool]
    [Description("Branch, upstream, ahead and behind counts, and changed paths for a git repo folder.")]
    public static async Task<string> GitStatus(
        [Description("Repo folder, e.g. the folder that holds the .sln")] string repoPath)
    {
        var status = await Service.StatusAsync(repoPath);
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

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
