using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Deletes the members and types that <c>ide_find_unused</c> reported and a person confirmed (<c>ide_remove_unused</c>, unit 8.1). Each
/// item is deleted by its own checked delete (DeleteEngine), in turn, from the lowest line of its file upward, so earlier deletions do not
/// move the lines of later ones. The first refusal stops the whole change, which is then not applied. Pure: the caller applies the result.
/// </summary>
internal static class RemoveUnusedEngine
{
    public static async Task<MethodEditPlan> PlanAsync(Solution solution, IReadOnlyList<(string File, int Line, string Kind)> items)
    {
        var plan = new MethodEditPlan();
        if (items.Count == 0)
        {
            plan.Reasons.Add(Strings.Get("RemoveUnusedNone"));
            return plan;
        }

        var ordered = items.OrderBy(i => i.File, System.StringComparer.OrdinalIgnoreCase).ThenByDescending(i => i.Line).ToList();
        var current = solution;
        foreach (var item in ordered)
        {
            var step = item.Kind == "type"
                ? await DeleteEngine.DeleteTypeAsync(current, item.File, item.Line, 1, new List<CallerEdit>(), new List<string>(), false).ConfigureAwait(false)
                : await DeleteEngine.DeleteMemberAsync(current, item.File, item.Line, 1, new List<CallerEdit>(), new List<string>(), _ => { }).ConfigureAwait(false);
            if (step.Refused || step.Result is null)
            {
                plan.Reasons.Add(Strings.Format("RemoveUnusedStepRefused", item.File, item.Line));
                plan.Reasons.AddRange(step.Reasons);
                return plan;
            }

            current = step.Result;
            plan.Files.AddRange(step.Files);
        }

        plan.Warnings.Add(Strings.Format("RemoveUnusedDone", items.Count));
        plan.Result = current;
        return plan;
    }
}
