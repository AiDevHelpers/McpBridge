using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Bridge;
using AiDevHelpers.Helpers.SourceControlHelper;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Git status and diff run at once. Writes are two-step: git_plan shows the command, git_confirm runs it.</summary>
/// <summary>Runs an approved git_confirm write in the open VS Code window for its folder. Null when no VS Code window has the folder open.</summary>
internal sealed class VsCodeGitRouter : IVsCodeWriteRouter
{
    public async Task<GitResult?> TryRunAsync(string repoPath, string vsCodeAction, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var full = Path.GetFullPath(repoPath);
        if (!SourceControlTools.IsVsCodeOpen(full))
        {
            return null;
        }

        var values = new Dictionary<string, object?>(arguments) { ["repoPath"] = full };
        var reply = await IdeInstanceRouter.CallAsync(vsCodeAction, values, full, ct, ide: "VsCode");

        using var doc = JsonDocument.Parse(reply);
        var root = doc.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
        {
            var result = root.TryGetProperty("result", out var r) ? r.GetRawText() : "{}";
            return new GitResult(true, 0, result);
        }

        // An empty error makes the service report the failure itself (no git.exe fallback).
        var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        return new GitResult(false, -1, error ?? string.Empty);
    }
}

[McpServerToolType]
internal sealed class SourceControlTools
{
    private static readonly SourceControlService Service = new(new ProcessGitRunner(), new AiDevHelpers.Common.FileRepoRulesSource(), new VsCodeGitRouter());
    private static readonly GitHubService GitHub = new(new ProcessGhRunner());

    [McpServerTool]
    [SafeTool]
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
        if (!IsVsCodeOpen(full))
        {
            return null;
        }

        return await IdeInstanceRouter.CallAsync("gitStatus", new { repoPath = full }, full, ct, ide: "VsCode");
    }

    // True when a running VS Code window has this folder open.
    internal static bool IsVsCodeOpen(string fullPath) => VsInstanceRegistry.ListRunning().Any(i =>
        string.Equals(i.Ide, "VsCode", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrEmpty(i.SolutionPath)
        && string.Equals(Path.GetFullPath(i.SolutionPath), fullPath, StringComparison.OrdinalIgnoreCase));

    [McpServerTool]
    [SafeTool]
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
    [SafeTool]
    [Description("Plan a git write and return its exact command and a token. Nothing runs until git_confirm. " +
                 "action: stage, commit, pull, push, create_branch, delete_branch, switch, stash_push, stash_pop, stash_apply, merge, merge_abort, worktree_add, worktree_remove. " +
                 "sequence: several of those actions under one token, e.g. [stage, commit, push]; they run in order at git_confirm " +
                 "and the first failure stops the rest. Give action or sequence, not both. paths, message and branch go to each step that needs them.")]
    public static async Task<string> GitPlan(
        [Description("Repo folder")] string repoPath,
        [Description("One of the write actions above (leave out when sequence is given)")] string action = "",
        [Description("Paths, for stage")] string[]? paths = null,
        [Description("Commit message, for commit")] string? message = null,
        [Description("Branch name, for push, create_branch, delete_branch and switch; the ref to merge in, for merge")] string? branch = null,
        [Description("Write actions to run in order under one token, e.g. stage, commit, push")] string[]? sequence = null,
        [Description("Destination for push: a branch name (pushes HEAD there) or source:destination. Not with branch.")] string? pushRef = null,
        [Description("Folder, for worktree_add and worktree_remove (a linked worktree of this repo)")] string? path = null,
        [Description("Branch that must already be merged into this ref, for delete_branch. Makes the delete a force delete (-D) after an ancestor check.")] string? mergedInto = null,
        CancellationToken ct = default)
    {
        var hasSequence = sequence is { Length: > 0 };
        var hasAction = !string.IsNullOrWhiteSpace(action);
        if (hasSequence == hasAction)
        {
            return Json(new { ok = false, error = Strings.Get("SequenceChoice") });
        }

        try
        {
            if (hasSequence)
            {
                var planned = await Service.PlanSequenceAsync(repoPath, sequence!, paths, message, branch, ct, pushRef);
                return Json(new { ok = true, token = planned.Token, sequence = sequence, command = planned.Description });
            }

            var pending = await Service.PlanAsync(repoPath, action!, paths, message, branch, ct, pushRef, path, mergedInto);
            return Json(new { ok = true, token = pending.Token, action = pending.Action, command = pending.Description });
        }
        catch (ArgumentException ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [SafeTool]
    [Description("Run a write planned by git_plan, once. The token is single use.")]
    public static async Task<string> GitConfirm(
        [Description("Token returned by git_plan")] string token)
    {
        var result = await Service.ConfirmAsync(token);
        return Json(new { ok = result.Ok, exitCode = result.ExitCode, output = result.Output });
    }

    [McpServerTool]
    [SafeTool]
    [Description("Status of each CI check of a pull request, read through the GitHub CLI (gh). Read only. " +
                 "bucket is pass, fail, pending, skipping or cancel. Use it instead of waiting in a loop.")]
    public static async Task<string> PrChecks(
        [Description("Repo folder")] string repoPath,
        [Description("Pull request number")] int pr,
        CancellationToken ct = default)
    {
        var result = await GitHub.ChecksAsync(repoPath, pr, ct);

        // The checks this repo's rules require (committed rules plus the cached live protection) that the pull request lacks.
        var root = AiDevHelpers.Common.RepoSettingsFile.FindRepoRootFromFolder(repoPath);
        var required = root is null ? [] : AiDevHelpers.Common.RepoRulesFile.Effective(root).RequiredChecks;
        var missing = result.Error is null ? RuleGuard.MissingChecks(required, result.Checks.Select(c => c.Name)) : [];
        return Json(new
        {
            ok = result.Error is null,
            count = result.Checks.Count,
            checks = result.Checks.Select(c => new { name = c.Name, state = c.State, bucket = c.Bucket, link = c.Link }),
            missingRequired = missing,
            error = result.Error,
        });
    }

    [McpServerTool]
    [SafeTool]
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
