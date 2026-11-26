using Microsoft.CodeAnalysis;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// The plan-then-confirm flow every edit action shares: plan on a snapshot with a time limit, refuse logical documents,
/// add diffs and the plan hash, then either return the plan (not confirmed, or refused) or apply it. The host supplies the
/// apply step (Visual Studio writes through the workspace; the helper writes to disk) and the post-apply file moves and deletes.
/// The reply shape is the same for every host.
/// </summary>
internal static class PlanFlow
{
    public static async Task<object> RunAsync(
        Solution current,
        Func<Solution, Task<MethodEditPlan>> plan,
        bool confirm,
        string? expectedHash,
        Func<Solution, Task> apply,
        Func<MethodEditPlan, (object Moved, object Deleted)> afterApply)
    {
        var outcome = await PlanRunner.RunAsync(current, plan, PlanRunner.DefaultTimeout).ConfigureAwait(true);
        var planned = outcome.Plan ?? PlanRunner.Incomplete(PlanRunner.DefaultTimeout);
        foreach (var path in planned.Files)
        {
            if (LogicalDocuments.RefusalFor(current, path) is { } refusal)
            {
                planned.Reasons.Add(refusal);
            }
        }

        foreach (var path in planned.Divergent)
        {
            planned.Warnings.Add(Strings.Format("LogicalDivergent", path));
        }

        if (planned.Result is { } changed)
        {
            var count = 0;
            foreach (var documentId in changed.GetChanges(current).GetProjectChanges().SelectMany(p => p.GetChangedDocuments()))
            {
                if (count++ >= UnifiedDiff.MaxFiles)
                {
                    planned.Diffs.Add(Strings.Format("DiffFilesTruncated", UnifiedDiff.MaxFiles));
                    break;
                }

                var before = await current.GetDocument(documentId)!.GetTextAsync().ConfigureAwait(true);
                var after = await changed.GetDocument(documentId)!.GetTextAsync().ConfigureAwait(true);
                var path = changed.GetDocument(documentId)!.FilePath ?? documentId.ToString();
                if (UnifiedDiff.Of(path, before.ToString(), after.ToString()) is { } diff)
                {
                    planned.Diffs.Add(diff);
                }
            }
        }

        planned.Hash = await PlanHash.ComputeAsync(current, planned.Result).ConfigureAwait(true);
        if (confirm && expectedHash is not null && !PlanHash.Matches(expectedHash, planned.Hash))
        {
            planned.Reasons.Add(Strings.Get("PlanHashChanged"));
        }

        if (planned.Refused || !confirm)
        {
            return new { planned = !planned.Refused, applied = false, check = planned.ToReply() };
        }

        await apply(planned.Result!).ConfigureAwait(true);

        // Files move on disk only after the text changes are in, and only when the plan asked for it.
        var (moved, deleted) = afterApply(planned);
        return new { planned = false, applied = true, check = planned.ToReply(), moved, deleted };
    }
}
