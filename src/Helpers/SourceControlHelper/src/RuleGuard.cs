using AiDevHelpers.Common;

namespace AiDevHelpers.Helpers.SourceControlHelper;

/// <summary>A planned write the repo's rules refuse. Branch is the branch the rule is about; Message says why and what to do instead.</summary>
public sealed record RuleRefusal(string Action, string Branch, string Message);

/// <summary>Thrown by <see cref="SourceControlService.PlanAsync"/> when a rule refuses the write. It is an argument error, so callers report it like one.</summary>
public sealed class RuleRefusedException(string message) : ArgumentException(message);

/// <summary>
/// Checks a planned git write against the repo's effective rules. Rules only restrict: with no rules, nothing is refused. There is no
/// force-push action in <see cref="SourceControlService.WriteActions"/>, so none is checked here.
/// </summary>
public static class RuleGuard
{
    /// <summary>The required check names that a pull request's checks do not include (names compared ignoring case).</summary>
    public static IReadOnlyList<string> MissingChecks(IEnumerable<string> required, IEnumerable<string> actual)
    {
        var have = actual.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. required.Where(r => !have.Contains(r))];
    }

    /// <summary>The required gate commands that no planned gate step runs. A step runs a command when its text contains it.</summary>
    public static IReadOnlyList<string> MissingGateCommands(IEnumerable<string> required, IEnumerable<string> stepCommands)
    {
        var steps = stepCommands.Select(s => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries))).ToList();
        return [.. required.Where(r => !steps.Any(s => s.Contains(string.Join(' ', r.Split(' ', StringSplitOptions.RemoveEmptyEntries)), StringComparison.OrdinalIgnoreCase)))];
    }

    /// <summary>The actions whose planning needs the checked-out branch.</summary>
    public static bool NeedsCurrentBranch(string action, string? branch) =>
        action == "commit" || action == "merge" || (action == "push" && string.IsNullOrWhiteSpace(branch));

    public static RuleRefusal? Check(EffectiveRules rules, string action, string? branch, string? currentBranch)
    {
        switch (action)
        {
            case "commit" when currentBranch is not null && rules.IsProtected(currentBranch):
                return new RuleRefusal(action, currentBranch, Strings.Format("RuleCommitOnProtected", currentBranch));
            // The merge target is the checked-out branch. An unknown branch is refused, not waved through.
            case "merge" when currentBranch is null:
                return new RuleRefusal(action, string.Empty, Strings.Get("RuleMergeUnknownBranch"));
            case "merge" when rules.IsProtected(currentBranch):
                return new RuleRefusal(action, currentBranch, Strings.Format("RuleMergeIntoProtected", currentBranch));
            case "push":
                var target = string.IsNullOrWhiteSpace(branch) ? currentBranch : branch;
                return target is not null && rules.IsProtected(target)
                    ? new RuleRefusal(action, target, Strings.Format("RulePushToProtected", target))
                    : null;
            case "delete_branch" when !string.IsNullOrWhiteSpace(branch) && rules.IsProtected(branch):
                return new RuleRefusal(action, branch, Strings.Format("RuleDeleteProtected", branch));
            case "create_branch" when !string.IsNullOrWhiteSpace(branch) && !rules.NameAllowed(branch):
                return new RuleRefusal(action, branch, Strings.Format("RuleBranchName", branch));
            default:
                return null;
        }
    }
}
