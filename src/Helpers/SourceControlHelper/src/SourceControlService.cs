using System.Collections.Concurrent;
using AiDevHelpers.Common;
using AiDevHelpers.Common.Processes;
using System.Text;

namespace AiDevHelpers.Helpers.SourceControlHelper;

/// <summary>Runs git. Injected so tests can see the exact arguments.</summary>
public interface IGitRunner
{
    Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default);
}

/// <summary>Result of one git call: exit code and combined stdout and stderr.</summary>
public sealed record GitResult(bool Ok, int ExitCode, string Output);

/// <summary>HEAD commit and branch name of a repo at one moment. Null when git cannot say (for example an unborn HEAD).</summary>
public sealed record RepoState(string? Head, string? Branch);

/// <summary>A write waiting for approval. Nothing runs until <see cref="SourceControlService.ConfirmAsync"/> is called with its token.</summary>
/// <param name="PlannedState">The repo state at plan time, for HEAD-sensitive actions. Null when the write is not guarded.</param>
public sealed record PendingWrite(
    string Token,
    string Action,
    string Description,
    IReadOnlyList<string> Arguments,
    string RepoPath,
    string? Branch = null,
    bool Overridden = false,
    RepoState? PlannedState = null,
    string? MergedInto = null);

/// <summary>Several writes planned under one token. They run in order at confirm and the first failure stops the rest.</summary>
/// <param name="PlannedState">The state at plan time when any step is HEAD-sensitive. Checked once, before the first step.</param>
public sealed record PendingSequence(string Token, IReadOnlyList<PendingWrite> Steps, string Description, RepoState? PlannedState = null);

/// <summary>
/// Runs an approved write in an open VS Code window. Returns null when no VS Code window has the folder open, so the write
/// runs through git.exe. Once a window has taken the write, its result is final: the caller never falls back to git.exe.
/// </summary>
public interface IVsCodeWriteRouter
{
    Task<GitResult?> TryRunAsync(string repoPath, string vsCodeAction, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct);
}

/// <summary>
/// Git status and diff run at once. Every write (stage, commit, pull, push, branch, switch, stash) is two-step:
/// Plan returns the exact git command and a token, and Confirm runs it once (D7, as in the extension Add flow).
/// Visual Studio has no public git write API, so this runs git.exe for both IDEs (see the plan's Phase 2 spike).
/// </summary>
public sealed class SourceControlService(IGitRunner git, IRepoRulesSource? rules = null, IVsCodeWriteRouter? vsCode = null)
{
    private readonly ConcurrentDictionary<string, PendingWrite> _pending = new();
    private readonly ConcurrentDictionary<string, PendingSequence> _sequences = new();

    public static readonly IReadOnlyList<string> WriteActions =
        ["stage", "commit", "pull", "push", "create_branch", "delete_branch", "switch", "stash_push", "stash_pop", "stash_apply", "merge", "merge_abort", "worktree_add", "worktree_remove"];

    public async Task<GitStatusResult> StatusAsync(string repoPath, CancellationToken ct = default)
    {
        var result = await git.RunAsync(repoPath, ["status", "--porcelain=v1", "--branch"], ct);
        if (result.Ok)
        {
            return GitStatusParser.Parse(result.Output);
        }

        var error = string.IsNullOrWhiteSpace(result.Output) ? Strings.Format("GitFailedSilently", result.ExitCode) : result.Output;
        return new GitStatusResult(null, null, 0, 0, [], error);
    }

    public Task<GitResult> DiffAsync(string repoPath, bool staged, string? path = null, CancellationToken ct = default)
    {
        var args = new List<string> { "diff" };
        if (staged)
        {
            args.Add("--cached");
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            args.Add("--");
            args.Add(path);
        }

        return git.RunAsync(repoPath, args, ct);
    }

    /// <summary>
    /// Plans a write and checks it against the repo's rules first, so a refused write never gets a token. A refusal records a request
    /// the person can grant in a console (<c>elev rules override</c>); the next matching plan then uses that waiver once. With no
    /// rules source, or no rules, it plans like <see cref="Plan"/>.
    /// </summary>
    public async Task<PendingWrite> PlanAsync(
        string repoPath,
        string action,
        IReadOnlyList<string>? paths = null,
        string? message = null,
        string? branch = null,
        CancellationToken ct = default,
        string? pushRef = null,
        string? path = null,
        string? mergedInto = null)
    {
        if (pushRef is not null && action != "push")
        {
            throw new ArgumentException(Strings.Get("PushRefNeedsPush"), nameof(pushRef));
        }

        if (action == "merge_abort" && !(await git.RunAsync(repoPath, ["rev-parse", "--verify", "-q", "MERGE_HEAD"], ct)).Ok)
        {
            throw new ArgumentException(Strings.Get("MergeAbortNotMerging"), nameof(action));
        }

        var overridden = await CheckRulesAsync(repoPath, action, pushRef is null ? branch : DestinationOf(pushRef), ct);
        if (action == "worktree_remove")
        {
            await CheckWorktreeRemoveAsync(repoPath, path, ct);
        }

        if (action == "delete_branch" && mergedInto is not null && branch is not null && !await IsAncestorAsync(repoPath, branch, mergedInto, ct))
        {
            throw new ArgumentException(Strings.Format("BranchNotMerged", branch, mergedInto), nameof(branch));
        }

        var planned = HeadSensitive.Contains(action) ? await ReadStateAsync(repoPath, ct) : null;
        return Register(BuildPending(repoPath, action, paths, message, branch, overridden, pushRef, path, mergedInto) with { PlannedState = planned });
    }

    // The path must be a linked worktree of this repo: listed by git, not the main worktree, and not the folder planned from.
    private async Task CheckWorktreeRemoveAsync(string repoPath, string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(Strings.Get("WorktreePathNeeded"), nameof(path));
        }

        var list = await git.RunAsync(repoPath, ["worktree", "list", "--porcelain"], ct);
        var listed = list.Output.Split('\n')
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(line => NormalizePath(line["worktree ".Length..]))
            .ToList();
        var target = NormalizePath(Path.Combine(repoPath, path));
        if (!listed.Contains(target, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(Strings.Get("WorktreeNotListed"), nameof(path));
        }

        if (listed.Count > 0 && string.Equals(listed[0], target, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(Strings.Get("WorktreeIsMain"), nameof(path));
        }

        if (string.Equals(NormalizePath(repoPath), target, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(Strings.Get("WorktreeIsCurrent"), nameof(path));
        }
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    // True when ancestor is reachable from descendant. Used before any force delete, at plan time and again at confirm.
    private async Task<bool> IsAncestorAsync(string repoPath, string ancestor, string descendant, CancellationToken ct) =>
        (await git.RunAsync(repoPath, ["merge-base", "--is-ancestor", ancestor, descendant], ct)).Ok;

    /// <summary>
    /// Plans several writes under one token. Each action is checked against the rules at plan time. paths, message and branch
    /// go to every step that needs them. Nothing runs until <see cref="ConfirmAsync"/>, which runs the steps in order and stops at the first failure.
    /// </summary>
    public async Task<PendingSequence> PlanSequenceAsync(
        string repoPath,
        IReadOnlyList<string> actions,
        IReadOnlyList<string>? paths = null,
        string? message = null,
        string? branch = null,
        CancellationToken ct = default,
        string? pushRef = null)
    {
        if (actions.Contains("merge_abort"))
        {
            // Abort is a recovery step for a merge that stopped; it cannot wait behind earlier steps in a sequence.
            throw new ArgumentException(Strings.Get("MergeAbortNotInSequence"), nameof(actions));
        }

        if (actions.Count == 0)
        {
            throw new ArgumentException(Strings.Get("SequenceNeedsSteps"), nameof(actions));
        }

        var steps = new List<PendingWrite>();
        foreach (var action in actions)
        {
            // pushRef goes only to push steps; other steps keep the plain branch.
            var stepPushRef = action == "push" ? pushRef : null;
            var overridden = await CheckRulesAsync(repoPath, action, stepPushRef is null ? branch : DestinationOf(stepPushRef), ct);
            steps.Add(BuildPending(repoPath, action, paths, message, branch, overridden, stepPushRef));
        }

        // One state read covers the whole sequence: it is checked before the first step only (see RunSequenceAsync).
        var planned = actions.Any(HeadSensitive.Contains) ? await ReadStateAsync(repoPath, ct) : null;
        var sequence = new PendingSequence(
            Token: Guid.NewGuid().ToString("N"),
            Steps: steps,
            Description: string.Join(" && ", steps.Select(s => s.Description)),
            PlannedState: planned);
        _sequences[sequence.Token] = sequence;
        return sequence;
    }

    /// <summary>Plans a write without checking the rules. Returns the exact command and a token; runs nothing. The tools use <see cref="PlanAsync"/>.</summary>
    public PendingWrite Plan(string repoPath, string action, IReadOnlyList<string>? paths = null, string? message = null, string? branch = null) =>
        Register(BuildPending(repoPath, action, paths, message, branch, overridden: false));

    // Returns true when a rule refused the write and a waiver was used for it. Throws when a rule refuses and no waiver is left.
    private async Task<bool> CheckRulesAsync(string repoPath, string action, string? branch, CancellationToken ct)
    {
        if (rules?.For(repoPath) is { } source && !source.Rules.IsEmpty && WriteActions.Contains(action))
        {
            var current = RuleGuard.NeedsCurrentBranch(action, branch) ? await CurrentBranchAsync(repoPath, ct) : null;
            if (RuleGuard.Check(source.Rules, action, branch, current) is { } refusal)
            {
                if (!RuleOverrides.TryConsume(source.Root, refusal.Action, refusal.Branch, DateTime.UtcNow))
                {
                    var id = RuleOverrides.Request(source.Root, refusal.Action, refusal.Branch, DateTime.UtcNow);
                    throw new RuleRefusedException(refusal.Message + " " + Strings.Format("RuleOverrideHint", source.Root, id));
                }

                return true;
            }
        }

        return false;
    }

    private PendingWrite Register(PendingWrite pending)
    {
        _pending[pending.Token] = pending;
        return pending;
    }

    // The destination branch of a push-to-ref: "dst" pushes HEAD to dst, "src:dst" pushes src to dst. Rules check the destination.
    internal static string DestinationOf(string pushRef) => pushRef.Split(':')[^1];

    // "dst" -> "HEAD:refs/heads/dst"; "src:dst" -> "src:refs/heads/dst". Anything else is refused.
    private static string PushRefspec(string pushRef)
    {
        var parts = pushRef.Split(':');
        if (parts.Length > 2)
        {
            throw new ArgumentException(Strings.Get("PushRefInvalid"), nameof(pushRef));
        }

        var source = parts.Length == 2 ? parts[0] : "HEAD";
        var destination = parts[^1];
        if (!IsValidRefName(source) || !IsValidRefName(destination))
        {
            throw new ArgumentException(Strings.Get("PushRefInvalid"), nameof(pushRef));
        }

        return source + ":refs/heads/" + destination;
    }

    // A conservative branch-name check: no leading dash, no "..", no spaces, only letters, digits, '/', '.', '_' and '-'.
    private static bool IsValidRefName(string name) =>
        name.Length > 0 && !name.StartsWith('-') && !name.Contains("..", StringComparison.Ordinal)
        && name.All(c => char.IsLetterOrDigit(c) || c is '/' or '.' or '_' or '-');

    private static PendingWrite BuildPending(string repoPath, string action, IReadOnlyList<string>? paths, string? message, string? branch, bool overridden, string? pushRef = null, string? path = null, string? mergedInto = null)
    {
        if (!WriteActions.Contains(action))
        {
            throw new ArgumentException(Strings.Format("UnknownWriteAction", action, string.Join(", ", WriteActions)), nameof(action));
        }

        var args = BuildArguments(action, paths, message, branch, pushRef, path, mergedInto);
        return new PendingWrite(
            Token: Guid.NewGuid().ToString("N"),
            Action: action,
            Description: "git " + string.Join(" ", args.Select(Quote)),
            Arguments: args,
            RepoPath: repoPath,
            Branch: pushRef is null ? branch : DestinationOf(pushRef),
            Overridden: overridden,
            MergedInto: mergedInto);
    }

    /// <summary>
    /// Runs a planned write, or a planned sequence, once. An unknown or reused token is refused. The rules are checked again here,
    /// so a rule added between plan and confirm is seen; a write that was waived at plan time is not checked again.
    /// </summary>
    public async Task<GitResult> ConfirmAsync(string token, CancellationToken ct = default)
    {
        if (_sequences.TryRemove(token, out var sequence))
        {
            return await RunSequenceAsync(sequence, ct);
        }

        if (!_pending.TryRemove(token, out var pending))
        {
            return new GitResult(false, -1, Strings.Get("UnknownToken"));
        }

        if (await RefusalAsync(pending, ct) is { } refusal)
        {
            return new GitResult(false, -1, Strings.Format("RulesChangedBeforeConfirm", refusal));
        }

        if (pending.PlannedState is { } planned && await ReadStateAsync(pending.RepoPath, ct) != planned)
        {
            return new GitResult(false, -1, Strings.Get("HeadMovedBeforeConfirm"));
        }

        // A force delete is only safe while the branch is still merged; the branch may have moved since planning.
        if (pending.MergedInto is { } into && pending.Branch is { } deleted && !await IsAncestorAsync(pending.RepoPath, deleted, into, ct))
        {
            return new GitResult(false, -1, Strings.Format("BranchNotMerged", deleted, into));
        }

        return await RunWriteAsync(pending, ct);
    }

    // Actions whose result depends on which commit or branch is checked out. Only these take a plan-time state.
    private static readonly HashSet<string> HeadSensitive = ["commit", "push", "pull", "delete_branch", "merge"];

    // HEAD and branch now. A failed rev-parse (an unborn HEAD, or no repo) reads as null, which still compares as a value.
    private async Task<RepoState> ReadStateAsync(string repoPath, CancellationToken ct)
    {
        var head = await git.RunAsync(repoPath, ["rev-parse", "HEAD"], ct);
        var branch = await git.RunAsync(repoPath, ["rev-parse", "--abbrev-ref", "HEAD"], ct);
        return new RepoState(head.Ok ? head.Output.Trim() : null, branch.Ok ? branch.Output.Trim() : null);
    }

    // Runs one approved write. The VS Code route takes it when a window has the folder open and VS Code does exactly what the
    // approved command does; otherwise git.exe runs it. The token is already spent, so a route failure is final.
    private async Task<GitResult> RunWriteAsync(PendingWrite pending, CancellationToken ct)
    {
        if (vsCode is not null && VsCodeRoute(pending) is { } route
            && await vsCode.TryRunAsync(pending.RepoPath, route.Action, route.Arguments, ct) is { } routed)
        {
            return routed.Ok || routed.Output.Length > 0
                ? routed
                : new GitResult(false, -1, Strings.Format("VsCodeRouteFailed", route.Action));
        }

        var result = await git.RunAsync(pending.RepoPath, pending.Arguments, ct);
        if (pending.Action == "merge" && !result.Ok)
        {
            // git prints CONFLICT lines only; the unmerged paths need their own call.
            var unmerged = await git.RunAsync(pending.RepoPath, ["diff", "--name-only", "--diff-filter=U"], ct);
            if (unmerged.Ok && unmerged.Output.Trim().Length > 0)
            {
                return new GitResult(false, result.ExitCode, result.Output + Strings.Get("MergeConflictedPaths") + unmerged.Output);
            }
        }

        return result;
    }

    // The VS Code action and arguments for an approved write, or null when the write stays on git.exe (push, pull, create_branch).
    // Each argument comes from the approved git arguments, so VS Code runs the same paths, message and branch.
    private static (string Action, IReadOnlyDictionary<string, object?> Arguments)? VsCodeRoute(PendingWrite pending)
    {
        var args = pending.Arguments;
        if (pending.Action == "delete_branch" && args.Count > 1 && args[1] == "-D")
        {
            return null; // The VS Code delete is non-force only, so a force delete stays on git.exe.
        }

        return pending.Action switch
        {
            "stage" => ("gitStage", new Dictionary<string, object?> { ["paths"] = args.Skip(2).ToArray() }),        // add -- <paths>
            "commit" => ("gitCommit", new Dictionary<string, object?> { ["message"] = args[2] }),                   // commit -m <message>
            "switch" => ("gitSwitch", new Dictionary<string, object?> { ["branch"] = args[1] }),                    // switch <branch>
            "delete_branch" => ("gitDeleteBranch", new Dictionary<string, object?> { ["branch"] = args[2] }),       // branch -d <branch>
            "stash_push" => ("gitStashPush", new Dictionary<string, object?>()),                                    // stash push
            "stash_pop" => ("gitStashPop", new Dictionary<string, object?>()),                                      // stash pop
            "stash_apply" => ("gitStashApply", new Dictionary<string, object?>()),                                  // stash apply
            _ => null,
        };
    }

    // Runs each step in order, logging each command. The first refused or failed step stops the sequence.
    private async Task<GitResult> RunSequenceAsync(PendingSequence sequence, CancellationToken ct)
    {
        var log = new StringBuilder();
        if (sequence.PlannedState is { } planned && await ReadStateAsync(sequence.Steps[0].RepoPath, ct) != planned)
        {
            return new GitResult(false, -1, Strings.Get("HeadMovedBeforeConfirm"));
        }

        foreach (var step in sequence.Steps)
        {
            log.Append("$ ").Append(step.Description).Append('\n');
            if (await RefusalAsync(step, ct) is { } refusal)
            {
                log.Append(Strings.Format("RulesChangedBeforeConfirm", refusal)).Append('\n');
                return new GitResult(false, -1, log.ToString());
            }

            var result = await RunWriteAsync(step, ct);
            log.Append(result.Output);
            if (!result.Ok)
            {
                return new GitResult(false, result.ExitCode, log.ToString());
            }

            if (!result.Output.EndsWith('\n'))
            {
                log.Append('\n');
            }
        }

        return new GitResult(true, 0, log.ToString());
    }

    // The refusal message when the rules now refuse a planned write, or null. A write waived at plan time is not checked again.
    private async Task<string?> RefusalAsync(PendingWrite pending, CancellationToken ct)
    {
        if (pending.Overridden || rules?.For(pending.RepoPath) is not { } source || source.Rules.IsEmpty)
        {
            return null;
        }

        var current = RuleGuard.NeedsCurrentBranch(pending.Action, pending.Branch) ? await CurrentBranchAsync(pending.RepoPath, ct) : null;
        return RuleGuard.Check(source.Rules, pending.Action, pending.Branch, current)?.Message;
    }

    // The checked-out branch, or null when git cannot say (a detached HEAD reports "HEAD", which no rule names).
    private async Task<string?> CurrentBranchAsync(string repoPath, CancellationToken ct)
    {
        var result = await git.RunAsync(repoPath, ["rev-parse", "--abbrev-ref", "HEAD"], ct);
        var text = result.Output.Trim();
        return result.Ok && text.Length > 0 && !text.Contains('\n') ? text : null;
    }

    public static IReadOnlyList<string> BuildArguments(string action, IReadOnlyList<string>? paths, string? message, string? branch, string? pushRef = null, string? path = null, string? mergedInto = null)
    {
        switch (action)
        {
            case "stage":
                RequireItems(paths, Strings.Get("StageNeedsPath"));
                return ["add", "--", .. paths!];
            case "commit":
                if (string.IsNullOrWhiteSpace(message))
                {
                    throw new ArgumentException(Strings.Get("CommitNeedsMessage"), nameof(message));
                }

                return ["commit", "-m", message!];
            case "pull":
                return ["pull"];
            case "push":
                if (pushRef is not null)
                {
                    if (!string.IsNullOrWhiteSpace(branch))
                    {
                        throw new ArgumentException(Strings.Get("PushRefWithBranch"), nameof(pushRef));
                    }

                    return ["push", "origin", PushRefspec(pushRef)];
                }

                return string.IsNullOrWhiteSpace(branch) ? ["push"] : ["push", "origin", branch!];
            case "create_branch":
                RequireText(branch, Strings.Get("CreateBranchNeedsName"));
                return ["switch", "-c", branch!];
            case "delete_branch":
                RequireText(branch, Strings.Get("DeleteBranchNeedsName"));
                // -D only with mergedInto, after the ancestor check in PlanAsync and ConfirmAsync.
                return mergedInto is null ? ["branch", "-d", branch!] : ["branch", "-D", branch!];
            case "worktree_add":
                RequireText(branch, Strings.Get("WorktreeAddNeedsBranch"));
                RequireText(path, Strings.Get("WorktreePathNeeded"));
                return ["worktree", "add", "-b", branch!, path!];
            case "worktree_remove":
                RequireText(path, Strings.Get("WorktreePathNeeded"));
                return ["worktree", "remove", path!];
            case "switch":
                RequireText(branch, Strings.Get("SwitchNeedsName"));
                return ["switch", branch!];
            case "stash_push":
                return ["stash", "push"];
            case "stash_pop":
                return ["stash", "pop"];
            case "stash_apply":
                return ["stash", "apply"];
            case "merge":
                // branch is the ref to merge in. The rule checks the current branch, which is the merge target.
                RequireText(branch, Strings.Get("MergeNeedsSource"));
                return ["merge", "--no-ff", "--no-edit", branch!];
            case "merge_abort":
                return ["merge", "--abort"];
            default:
                throw new ArgumentException(Strings.Format("UnknownWriteActionName", action), nameof(action));
        }
    }

    private static void RequireItems(IReadOnlyList<string>? items, string message)
    {
        if (items is null || items.Count == 0)
        {
            throw new ArgumentException(message);
        }
    }

    private static void RequireText(string? text, string message)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException(message);
        }
    }

    private static string Quote(string argument) =>
        argument.Any(char.IsWhiteSpace) ? "\"" + argument + "\"" : argument;
}

/// <summary>Runs git.exe with an argument list (no shell), capturing stdout and stderr through the shared Common runner.</summary>
public sealed class ProcessGitRunner : IGitRunner
{
    public async Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        var result = await CommandRunner.RunAsync(
            new CommandRequest
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                // Never wait on a credential prompt, and stop a call that goes silent (no tty to answer it).
                Environment = new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0" },
                NoOutputTimeout = TimeSpan.FromMinutes(5),
            }, ct);
        if (!result.Launched)
        {
            throw new InvalidOperationException(Strings.Get("CouldNotStartGit"));
        }

        var output = result.StdOut + result.StdErr;
        if (result.TimedOut)
        {
            output = Strings.Get("GitTimedOut") + output;
        }

        return new GitResult(result.Success, result.ExitCode, output);
    }
}
