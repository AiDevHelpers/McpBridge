using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Replaces every call of a value-returning method with its body and deletes the method (<c>ide_inline_method</c>, unit 6.6). Only a method
/// whose body is one return expression (or an expression body) is inlined. Each argument replaces its parameter; an argument that is not a
/// literal or a name is refused when its parameter is used more than once. Refused for a recursive method, one that overrides, implements
/// or is overridden, an interface member, a params, ref or out parameter, and a reference that is not a plain call. Pure: the caller applies the result.
/// </summary>
internal static class InlineMethodEngine
{
    public static async Task<MethodEditPlan> PlanAsync(Solution solution, string file, int line, int column)
    {
        var plan = new MethodEditPlan();
        var target = await EditTarget.LocateAsync(solution, file, line, column, new[] { typeof(MethodDeclarationSyntax) }, r => plan.Reasons.Add(r))
            .ConfigureAwait(false);
        if (target is null)
        {
            return plan;
        }

        if (target.Symbol is not IMethodSymbol symbol || target.Declaration is not MethodDeclarationSyntax declaration)
        {
            plan.Reasons.Add(Strings.Get("MethodEditNotAMethod"));
            return plan;
        }

        if (symbol.ContainingType.TypeKind == TypeKind.Interface || symbol.ReturnsVoid || symbol.IsAsync || symbol.Parameters.Any(p => p.IsParams || p.RefKind != RefKind.None))
        {
            plan.Reasons.Add(Strings.Get("InlineUnsupportedMethod"));
            return plan;
        }

        var body = BodyExpression(declaration);
        if (body is null)
        {
            plan.Reasons.Add(Strings.Get("InlineNotOneExpression"));
            return plan;
        }

        var related = await CoverageOfReferences.RelatedAsync(solution, symbol).ConfigureAwait(false);
        if (related.Count > 0)
        {
            plan.Reasons.Add(Strings.Format("MoveRelated", string.Join(", ", related)));
            return plan;
        }

        var documentId = target.DocumentId;
        var model = await solution.GetDocument(documentId)!.GetSemanticModelAsync().ConfigureAwait(false);
        var references = await MethodEditEngine.CollectReferencesAsync(solution, symbol).ConfigureAwait(false);
        var inside = references.Where(r => r.DocumentId == documentId && declaration.Span.Contains(r.Span)).ToList();
        if (inside.Count > 0)
        {
            plan.Reasons.Add(Strings.Get("InlineRecursive"));
            return plan;
        }

        references.RemoveAll(r => inside.Contains(r));
        plan.ReferenceCount = references.Count;
        var calls = new List<CallerEdit>();
        var changes = new Dictionary<DocumentId, List<TextChange>>();
        var parameterNames = symbol.Parameters.Select(p => p.Name).ToList();
        foreach (var reference in references)
        {
            var document = solution.GetDocument(reference.DocumentId);
            var root = document is null ? null : await document.GetSyntaxRootAsync().ConfigureAwait(false);
            var call = root is null ? null : CallSiteRewriter.CallOf(root.FindToken(reference.Span.Start)) as InvocationExpressionSyntax;
            if (call is null || call.ArgumentList.Arguments.Any(a => a.NameColon is not null) || call.ArgumentList.Arguments.Count != parameterNames.Count)
            {
                plan.Uncovered.Add(new { file = reference.File, line = reference.Line, column = reference.Column, reason = Strings.Get("InlineNotPlainCall") });
                continue;
            }

            var substituted = Substitute(body, parameterNames, call.ArgumentList.Arguments.Select(a => a.Expression).ToList(), model!, out var refusal);
            if (refusal is not null)
            {
                plan.Reasons.Add(refusal);
                return plan;
            }

            calls.Add(new CallerEdit(reference.File, reference.Line, reference.Column, "(" + substituted + ")"));
        }

        if (plan.Uncovered.Count > 0)
        {
            plan.Reasons.Add(Strings.Format("MethodEditUncovered", plan.Uncovered.Count));
            return plan;
        }

        if (!await MethodEditEngine.AddCallerEditsAsync(solution, references, calls, changes, plan).ConfigureAwait(false))
        {
            return plan;
        }

        if (!changes.ContainsKey(documentId))
        {
            changes[documentId] = new List<TextChange>();
        }

        changes[documentId].Add(new TextChange(declaration.FullSpan, ""));
        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        foreach (var id in changes.Keys)
        {
            edited = await EditChecks.FormatFileAsync(edited, id, plan).ConfigureAwait(false);
        }

        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, changes.Keys.Concat(references.Select(r => r.DocumentId)), plan).ConfigureAwait(false);
        plan.Files.AddRange(changes.Keys.Select(id => solution.GetDocument(id)!.FilePath ?? id.ToString()));
        plan.Warnings.Add(Strings.Format("InlineDone", symbol.Name, references.Count));
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    // The single value expression of a method: its expression body, or the one return statement of its block.
    private static ExpressionSyntax? BodyExpression(MethodDeclarationSyntax declaration)
    {
        if (declaration.ExpressionBody is not null)
        {
            return declaration.ExpressionBody.Expression;
        }

        if (declaration.Body is { Statements.Count: 1 } block && block.Statements[0] is ReturnStatementSyntax { Expression: { } value })
        {
            return value;
        }

        return null;
    }

    // The body with each parameter name replaced by its argument. An argument that is not a literal or a name is refused when the
    // parameter is used more than once, since it would run more than once.
    private static string Substitute(ExpressionSyntax body, IReadOnlyList<string> parameters, IReadOnlyList<ExpressionSyntax> arguments, SemanticModel model, out string? refusal)
    {
        refusal = null;
        var uses = body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Where(id => parameters.Contains(id.Identifier.ValueText, StringComparer.Ordinal))
            .GroupBy(id => id.Identifier.ValueText, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        for (var i = 0; i < parameters.Count; i++)
        {
            var argument = arguments[i];
            var simple = argument is LiteralExpressionSyntax or IdentifierNameSyntax;
            if (!simple && uses.TryGetValue(parameters[i], out var count) && count > 1)
            {
                refusal = Strings.Format("InlineArgumentRepeated", parameters[i]);
                return "";
            }
        }

        var rewritten = body.ReplaceNodes(body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Where(id => parameters.Contains(id.Identifier.ValueText, StringComparer.Ordinal)),
            (original, _) =>
            {
                var index = parameters.ToList().IndexOf(original.Identifier.ValueText);
                var argument = arguments[index];
                return SyntaxFactory.ParseExpression(argument.WithoutTrivia().ToString());
            });
        return rewritten.WithoutTrivia().ToString();
    }
}
