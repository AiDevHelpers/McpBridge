using Microsoft.CodeAnalysis;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>Plans a method edit together with the members it needs: adds those members first, then plans the method on the result. Shared by the VS bridge and the Roslyn helper.</summary>
internal static class MemberPlanning
{
    // Adds the fields and properties a method needs to its type first, then plans the method on top, shifting its line numbers past
    // the inserted lines. The method plan checks diagnostics against the original solution, so members' own warnings are reported too.
    internal static async Task<MethodEditPlan> WithMembersAsync(
        Solution solution, string file, int line, IReadOnlyList<MemberSpec> members,
        Func<Solution, Func<int, int>, Solution, Task<MethodEditPlan>> plan)
    {
        if (members.Count == 0)
        {
            return await plan(solution, l => l, solution).ConfigureAwait(false);
        }

        var added = await MemberAddEngine.PlanAsync(solution, file, line, members, finalize: false).ConfigureAwait(false);
        if (added.Refused)
        {
            return added;
        }

        var result = await plan(added.Result!, l => l > added.InsertedAfterLine ? l + added.InsertedLines : l, solution).ConfigureAwait(false);
        result.MembersAdded.AddRange(members.Select(m => m.Name));
        result.Warnings.AddRange(added.Warnings);
        foreach (var f in added.Files.Where(f => !result.Files.Contains(f)))
        {
            result.Files.Add(f);
        }

        return result;
    }
}
