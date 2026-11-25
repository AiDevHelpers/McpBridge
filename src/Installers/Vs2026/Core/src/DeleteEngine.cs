using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Plans deleting a member: a method, property, field, event, constructor, operator, indexer, enum member or interface member. A delete
/// is refused while any reference remains (the member's own body is not a reference), while it overrides or implements something, while an
/// interface member has implementers, and while tests call it unless <c>deleteTests</c> names them. Pure: the caller applies the result.
/// </summary>
internal static class DeleteEngine
{
    public static async Task<MethodEditPlan> DeleteMemberAsync(
        Solution solution, string file, int line, int column, IReadOnlyList<CallerEdit> callerEdits, IReadOnlyList<string> deleteTests, Action<string> cancelled)
    {
        var plan = new MethodEditPlan();
        var target = await EditTarget.LocateAsync(solution, file, line, column, EditTarget.AllMemberKinds, r => plan.Reasons.Add(r)).ConfigureAwait(false);
        if (target is null)
        {
            return plan;
        }

        var symbol = target.Symbol;
        plan.OldSignature = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        plan.SignatureChanged = true;

        // Overrides and implementations: a delete there would change the contract, so it is refused.
        var related = await CoverageOfReferences.RelatedAsync(solution, symbol).ConfigureAwait(false);
        if (related.Count > 0)
        {
            plan.Reasons.Add(Strings.Format("DeleteRelated", string.Join(", ", related)));
        }

        if (symbol.ContainingType?.TypeKind == TypeKind.Interface)
        {
            var implementers = await SymbolFinder.FindImplementationsAsync(symbol, solution).ConfigureAwait(false);
            if (implementers.Any())
            {
                plan.Reasons.Add(Strings.Format("DeleteHasImplementers", implementers.Count()));
            }
        }

        var references = await MethodEditEngine.CollectReferencesAsync(solution, symbol).ConfigureAwait(false);
        references.RemoveAll(r => r.DocumentId == target.DocumentId && target.Declaration.Span.Contains(r.Span));
        plan.ReferenceCount = references.Count;

        var tests = await TestDiscovery.FindDirectAsync(solution, references).ConfigureAwait(false);
        var untested = tests.Where(t => !deleteTests.Contains(t.Name, StringComparer.Ordinal)).ToList();
        plan.Tests.AddRange(tests);
        if (untested.Count > 0)
        {
            plan.Reasons.Add(Strings.Format("DeleteTestsCall", string.Join(", ", untested.Select(t => t.Name))));
        }

        if (plan.Refused)
        {
            return plan;
        }

        // Each reference is covered by a caller edit (which may rewrite or remove the call), or the delete is refused.
        var changes = new Dictionary<DocumentId, List<TextChange>>
        {
            [target.DocumentId] = new List<TextChange>(),
        };
        if (!await MethodEditEngine.AddCallerEditsAsync(solution, references, callerEdits, changes, plan).ConfigureAwait(false))
        {
            return plan;
        }

        plan.Uncovered.AddRange(CoverageOfReferences.Uncovered(references, callerEdits));
        if (plan.Uncovered.Count > 0)
        {
            plan.Reasons.Add(Strings.Format("DeleteUncovered", plan.Uncovered.Count));
            return plan;
        }

        changes[target.DocumentId].Add(new TextChange(target.Declaration.FullSpan, ""));
        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        plan.Files.AddRange(changes.Keys.Select(id => solution.GetDocument(id)!.FilePath ?? id.ToString()));
        plan.DeclarationCount = 1;

        edited = await EditChecks.FormatFileAsync(edited, target.DocumentId, plan).ConfigureAwait(false);
        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, changes.Keys.Concat(references.Select(r => r.DocumentId)), plan).ConfigureAwait(false);
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }
}
