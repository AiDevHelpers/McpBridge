using System.Text.Json;
using AiDevHelpers.Installers.Vs2026.Core;
using Microsoft.CodeAnalysis;

namespace AiDevHelpers.Helpers.RoslynHelper;

/// <summary>
/// Group A edit actions: editMethod, editMember and addMember. Each one plans on the helper's current solution through
/// <see cref="PlanFlow"/>, and applies only when the caller confirms with the plan hash it was shown.
/// </summary>
public static class PlanActions
{
    public static void Register(ActionDispatcher dispatcher, Func<Solution?> current, Action<Solution> setCurrent)
    {
        dispatcher.Register("editMethod", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            var file = ArgumentParsers.RequireString(a, "file");
            var line = ArgumentParsers.RequireInt(a, "line");
            var column = ArgumentParsers.OptionalInt(a, "column") ?? 1;
            var newText = ArgumentParsers.RequireString(a, "newMethodText");
            var callerEdits = ArgumentParsers.ParseCallerEdits(a);
            var docs = ArgumentParsers.ParseDocUpdates(a);
            var members = ArgumentParsers.ParseMemberSpecs(a);
            var rewriteCallers = ArgumentParsers.OptionalBool(a, "rewriteCallers") == true;
            return MemberPlanning.WithMembersAsync(solution, file, line, members, (s, shift, baseline) => rewriteCallers
                ? ChangeSignatureEngine.PlanEditAsync(s, file, shift(line), column, newText, callerEdits, docs, baseline)
                : MethodEditEngine.PlanAsync(s, file, shift(line), column, newText, callerEdits, docs, baseline));
        }));

        dispatcher.Register("editMember", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            var file = ArgumentParsers.RequireString(a, "file");
            var line = ArgumentParsers.RequireInt(a, "line");
            return MemberEditEngine.PlanAsync(solution, file, line, ArgumentParsers.ParseMemberEdit(a), ArgumentParsers.ParseCallerEdits(a));
        }));

        dispatcher.Register("addMember", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            var file = ArgumentParsers.RequireString(a, "file");
            var line = ArgumentParsers.RequireInt(a, "line");
            return MemberAddEngine.PlanAsync(solution, file, line, ArgumentParsers.ParseMemberSpecs(a), finalize: true);
        }));
    }

    private static async Task<object> RunAsync(
        JsonElement args,
        Func<Solution?> current,
        Action<Solution> setCurrent,
        Func<Solution, JsonElement, Task<MethodEditPlan>> plan)
    {
        var snapshot = current() ?? throw new InvalidOperationException("No solution is loaded. Call loadSolution first.");
        var confirm = ArgumentParsers.OptionalBool(args, "confirm") == true;
        var planHash = ArgumentParsers.OptionalString(args, "planHash");
        Solution? applied = null;

        var reply = await PlanFlow.RunAsync(
            snapshot,
            solution => plan(solution, args),
            confirm,
            planHash,
            apply: async planned =>
            {
                var result = await PlanApplier.ApplyAsync(snapshot, planned).ConfigureAwait(false);
                if (!result.Applied)
                {
                    throw new InvalidOperationException(result.Refusal ?? "The plan was not applied.");
                }

                applied = planned;
            },
            afterApply: _ => (new List<object>(), new List<object>())).ConfigureAwait(false);

        if (applied is not null)
        {
            setCurrent(applied);
        }

        return reply;
    }
}
