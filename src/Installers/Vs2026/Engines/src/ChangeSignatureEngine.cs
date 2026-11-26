using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Changes a method's parameter list and rewrites its call sites to match (<c>ide_change_signature</c>). Arguments move with their
/// parameters, removed parameters' arguments are dropped when they cannot have side effects, and a new parameter takes its
/// <c>valueForCallers</c>. Calls the rewriter cannot handle refuse the change. The rest of the checks (compile errors, doc tags, tests)
/// are <see cref="MethodEditEngine"/>'s.
/// </summary>
internal static class ChangeSignatureEngine
{
    public static async Task<MethodEditPlan> PlanAsync(
        Solution solution, string file, int line, int column, IReadOnlyList<NewParameter> newParameters, DocUpdates? docs = null, HeaderChange? header = null)
    {
        var plan = new MethodEditPlan();
        header ??= new HeaderChange();
        if (ChangeSignatureHeader.Validate(header) is { } badHeader)
        {
            plan.Reasons.Add(badHeader);
            return plan;
        }

        var target = await EditTarget.LocateAsync(solution, file, line, column, EditTarget.AllMemberKinds, reason => plan.Reasons.Add(reason))
            .ConfigureAwait(false);
        if (target is null)
        {
            return plan;
        }

        if (target.Symbol is not IMethodSymbol method || target.Declaration is not MethodDeclarationSyntax declaration)
        {
            plan.Reasons.Add(Strings.Get("MethodEditNotAMethod"));
            return plan;
        }

        var kept = newParameters.Where(p => p.FromOld is not null).Select(p => p.FromOld!.Value).ToList();
        if (kept.Distinct().Count() != kept.Count || kept.Any(i => i < 0 || i >= method.Parameters.Length))
        {
            plan.Reasons.Add(Strings.Get("ChangeSignatureBadIndex"));
            return plan;
        }

        foreach (var parameter in newParameters)
        {
            if (SyntaxFactory.ParseParameterList("(" + parameter.Declaration + ")").GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                plan.Reasons.Add(Strings.Format("ChangeSignatureBadDeclaration", parameter.Declaration));
                return plan;
            }
        }

        var family = await ChangeSignatureFamily.FindAsync(solution, method).ConfigureAwait(false);
        if (family.Count > 1)
        {
            return await ChangeSignatureFamily.PlanAsync(solution, file, line, column, newParameters, family, docs, header).ConfigureAwait(false);
        }

        var list = SyntaxFactory.ParseParameterList("(" + string.Join(", ", newParameters.Select(p => p.Declaration)) + ")");
        var newMethodText = ChangeSignatureHeader.NewText(declaration, list.ToString(), header);

        // Call sites are rewritten before the method is planned. References inside the method itself are covered by its new text.
        var references = await MethodEditEngine.CollectReferencesAsync(solution, method).ConfigureAwait(false);
        references.RemoveAll(r => r.DocumentId == target.DocumentId && target.Declaration.Span.Contains(r.Span));
        var (edits, uncovered) = await CallSiteRewriter.RewriteAsync(solution, method, references, newParameters).ConfigureAwait(false);
        if (uncovered.Count > 0)
        {
            plan.Uncovered.AddRange(uncovered);
            plan.Reasons.Add(Strings.Format("MethodEditUncovered", uncovered.Count));
            return plan;
        }

        return await MethodEditEngine.PlanAsync(solution, file, line, column, newMethodText, edits, docs, null).ConfigureAwait(false);
    }

    /// <summary>
    /// ide_edit_method with <c>rewriteCallers</c>: the new parameter list is matched to the old one by name, and the call sites are
    /// rewritten to match. Only removals and reorders map cleanly. An added, renamed or retyped parameter needs a value or a decision
    /// a person must make, so it is refused and ide_change_signature is named instead.
    /// </summary>
    public static async Task<MethodEditPlan> PlanEditAsync(
        Solution solution, string file, int line, int column, string newMethodText, IReadOnlyList<CallerEdit> callerEdits, DocUpdates? docs, Solution? baseline)
    {
        var plan = new MethodEditPlan();
        if (callerEdits.Count > 0)
        {
            plan.Reasons.Add(Strings.Get("RewriteCallersWithCallerEdits"));
            return plan;
        }

        var target = await EditTarget.LocateAsync(solution, file, line, column, EditTarget.AllMemberKinds, reason => plan.Reasons.Add(reason))
            .ConfigureAwait(false);
        if (target is null)
        {
            return plan;
        }

        if (target.Symbol is not IMethodSymbol method || target.Declaration is not MethodDeclarationSyntax declaration)
        {
            plan.Reasons.Add(Strings.Get("MethodEditNotAMethod"));
            return plan;
        }

        // Text that does not parse as one method is left to MethodEditEngine, which reports it.
        if (SyntaxFactory.ParseMemberDeclaration(newMethodText) is not MethodDeclarationSyntax parsed)
        {
            return await MethodEditEngine.PlanAsync(solution, file, line, column, newMethodText, callerEdits, docs, baseline).ConfigureAwait(false);
        }

        var oldParameters = declaration.ParameterList.Parameters.ToList();
        var mapped = new List<NewParameter>();
        foreach (var parameter in parsed.ParameterList.Parameters)
        {
            var old = oldParameters.FindIndex(o => o.Identifier.ValueText == parameter.Identifier.ValueText);
            if (old < 0 || Shape(oldParameters[old]) != Shape(parameter))
            {
                plan.Reasons.Add(Strings.Get("RewriteCallersNeedsChangeSignature"));
                return plan;
            }

            mapped.Add(new NewParameter(parameter.WithoutTrivia().ToString(), old, null));
        }

        var unchanged = mapped.Count == oldParameters.Count && mapped.Select((m, i) => m.FromOld == i).All(same => same);
        if (unchanged)
        {
            return await MethodEditEngine.PlanAsync(solution, file, line, column, newMethodText, callerEdits, docs, baseline).ConfigureAwait(false);
        }

        var references = await MethodEditEngine.CollectReferencesAsync(solution, method).ConfigureAwait(false);
        references.RemoveAll(r => r.DocumentId == target.DocumentId && target.Declaration.Span.Contains(r.Span));
        var (edits, uncovered) = await CallSiteRewriter.RewriteAsync(solution, method, references, mapped).ConfigureAwait(false);
        if (uncovered.Count > 0)
        {
            plan.Uncovered.AddRange(uncovered);
            plan.Reasons.Add(Strings.Format("MethodEditUncovered", uncovered.Count));
            return plan;
        }

        return await MethodEditEngine.PlanAsync(solution, file, line, column, newMethodText, edits, docs, baseline).ConfigureAwait(false);
    }

    // What a parameter is for a caller: its modifiers and type. Name and default are compared elsewhere.
    private static string Shape(ParameterSyntax parameter) =>
        string.Join(" ", parameter.Modifiers.Select(m => m.ValueText)) + "|" + parameter.Type?.WithoutTrivia().ToString();
}
