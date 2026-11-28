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

/// <summary>A write waiting for approval. Nothing runs until <see cref="SourceControlService.ConfirmAsync"/> is called with its token.</summary>
public sealed record PendingWrite(
    string Token,
    string Action,
    string Description,
    IReadOnlyList<string> Arguments,
    string RepoPath,
    string? Branch = null,
    bool Overridden = false);

/// <summary>Several writes planned under one token. They run in order at confirm and the first failure stops the rest.</summary>
public sealed record PendingSequence(string Token, IReadOnlyList<PendingWrite> Steps, string Description);

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
        ["stage", "commit", "pull", "push", "create_branch", "delete_branch", "switch", "stash_push", "stash_pop", "stash_apply"];

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

    /// <summary>Per-file stats, totals and the raw diff. A path must stay inside the repo: absolute, rooted or "..". paths are refused before git runs.</summary>
    public async Task<DiffSummary> SummarizeDiffAsync(string repoPath, bool staged = false, string? path = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(path) && !IsInsideRepo(path))
        {
            return DiffSummary.Failed(Strings.Get("DiffPathOutside"));
        }

        var numstat = await git.RunAsync(repoPath, DiffArgs(staged, path, "--numstat"), ct);
        if (!numstat.Ok)
        {
            return DiffSummary.Failed(GitFailure(numstat));
        }

        var raw = await git.RunAsync(repoPath, DiffArgs(staged, path), ct);
        if (!raw.Ok)
        {
            return DiffSummary.Failed(GitFailure(raw));
        }

        var files = DiffStatParser.Parse(numstat.Output);
        return new DiffSummary(true, null, files, files.Sum(f => f.Added), files.Sum(f => f.Deleted), raw.Output);
    }

    private static IReadOnlyList<string> DiffArgs(bool staged, string? path, params string[] extra)
    {
        var args = new List<string> { "diff" };
        args.AddRange(extra);
        if (staged)
        {
            args.Add("--cached");
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            args.Add("--");
            args.Add(path);
        }

        return args;
    }

    private static bool IsInsideRepo(string path) =>
        !path.StartsWith('-') && !Path.IsPathRooted(path) && !path.Split('/', '\\').Contains("..");

    private static string GitFailure(GitResult result) =>
        string.IsNullOrWhiteSpace(result.Output) ? Strings.Format("GitFailedSilently", result.ExitCode) : result.Output;

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
        CancellationToken ct = default)
    {
        var overridden = await CheckRulesAsync(repoPath, action, branch, ct);
        return Register(BuildPending(repoPath, action, paths, message, branch, overridden));
    }

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
        CancellationToken ct = default)
    {
        if (actions.Count == 0)
        {
            throw new ArgumentException(Strings.Get("SequenceNeedsSteps"), nameof(actions));
        }

        var steps = new List<PendingWrite>();
        foreach (var action in actions)
        {
            var overridden = await CheckRulesAsync(repoPath, action, branch, ct);
            steps.Add(BuildPending(repoPath, action, paths, message, branch, overridden));
        }

        var sequence = new PendingSequence(
            Token: Guid.NewGuid().ToString("N"),
            Steps: steps,
            Description: string.Join(" && ", steps.Select(s => s.Description)));
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

    private static PendingWrite BuildPending(string repoPath, string action, IReadOnlyList<string>? paths, string? message, string? branch, bool overridden)
    {
        if (!WriteActions.Contains(action))
        {
            throw new ArgumentException(Strings.Format("UnknownWriteAction", action, string.Join(", ", WriteActions)), nameof(action));
        }

        var args = BuildArguments(action, paths, message, branch);
        return new PendingWrite(
            Token: Guid.NewGuid().ToString("N"),
            Action: action,
            Description: "git " + string.Join(" ", args.Select(Quote)),
            Arguments: args,
            RepoPath: repoPath,
            Branch: branch,
            Overridden: overridden);
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

        return await RunWriteAsync(pending, ct);
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

        return await git.RunAsync(pending.RepoPath, pending.Arguments, ct);
    }

    // The VS Code action and arguments for an approved write, or null when the write stays on git.exe (push, pull, create_branch).
    // Each argument comes from the approved git arguments, so VS Code runs the same paths, message and branch.
    private static (string Action, IReadOnlyDictionary<string, object?> Arguments)? VsCodeRoute(PendingWrite pending)
    {
        var args = pending.Arguments;
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

    public static IReadOnlyList<string> BuildArguments(string action, IReadOnlyList<string>? paths, string? message, string? branch)
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
                return string.IsNullOrWhiteSpace(branch) ? ["push"] : ["push", "origin", branch!];
            case "create_branch":
                RequireText(branch, Strings.Get("CreateBranchNeedsName"));
                return ["switch", "-c", branch!];
            case "delete_branch":
                RequireText(branch, Strings.Get("DeleteBranchNeedsName"));
                return ["branch", "-d", branch!];
            case "switch":
                RequireText(branch, Strings.Get("SwitchNeedsName"));
                return ["switch", branch!];
            case "stash_push":
                return ["stash", "push"];
            case "stash_pop":
                return ["stash", "pop"];
            case "stash_apply":
                return ["stash", "apply"];
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
            new CommandRequest { FileName = "git", Arguments = arguments, WorkingDirectory = workingDirectory }, ct);
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
