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
/// Makes a method async and awaits its callers (<c>ide_convert</c> kind make-async, unit 7.5). The method's header gets "async" and a
/// Task or Task&lt;T&gt; return type. Each direct call is written as "await call". A caller that is not async becomes async too, and its
/// own callers are handled the same way, up to <c>depth</c> levels; a call above that depth is refused, since it would need an await
/// the planner does not write. With <c>suffix</c>, the method is renamed to end in Async and so are its call sites. Calls inside lambdas,
/// method groups and nameof are refused. Pure: the caller applies <see cref="MethodEditPlan.Result"/>.
/// </summary>
internal static class AsyncEngine
{
    public static async Task<MethodEditPlan> PlanAsync(Solution solution, string file, int line, int depth, bool suffix)
    {
        var plan = new MethodEditPlan();
        if (depth < 0)
        {
            plan.Reasons.Add(Strings.Get("AsyncDepthNegative"));
            return plan;
        }

        var documentId = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault();
        var document = documentId is null ? null : solution.GetDocument(documentId);
        if (document is null)
        {
            plan.Reasons.Add(Strings.Format("NoRoslynDocumentFor", file));
            return plan;
        }

        var text = await document.GetTextAsync().ConfigureAwait(false);
        if (line < 1 || line > text.Lines.Count)
        {
            plan.Reasons.Add(Strings.Format("LineOutOfRange", line, file));
            return plan;
        }

        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        var lineText = text.Lines[line - 1].ToString();
        var position = text.Lines[line - 1].Start + (lineText.Length - lineText.TrimStart().Length);
        var target = root!.FindToken(Math.Min(position, root.FullSpan.End)).Parent?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (target is null || model!.GetDeclaredSymbol(target) is not IMethodSymbol symbol)
        {
            plan.Reasons.Add(Strings.Get("ConvertNotAMethod"));
            return plan;
        }

        if (!Eligible(symbol))
        {
            plan.Reasons.Add(Strings.Get("ConvertAsyncShape"));
            return plan;
        }

        var changes = new Dictionary<DocumentId, List<TextChange>>();
        void Add(DocumentId id, TextChange change)
        {
            if (!changes.TryGetValue(id, out var list))
            {
                changes[id] = list = new List<TextChange>();
            }

            list.Add(change);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(IMethodSymbol Symbol, MethodDeclarationSyntax Declaration, DocumentId Document, int Level)>();
        queue.Enqueue((symbol, target, documentId, 0));
        var newName = symbol.Name.EndsWith("Async", StringComparison.Ordinal) ? symbol.Name : symbol.Name + "Async";
        while (queue.Count > 0)
        {
            var (method, declaration, declarationDocument, level) = queue.Dequeue();
            if (!seen.Add(method.OriginalDefinition.ToDisplayString()))
            {
                continue;
            }

            Add(declarationDocument, Header(declaration, method));
            if (suffix && level == 0)
            {
                Add(declarationDocument, new TextChange(declaration.Identifier.Span, newName));
            }

            var references = await MethodEditEngine.CollectReferencesAsync(solution, method).ConfigureAwait(false);
            references.RemoveAll(r => r.DocumentId == declarationDocument && declaration.Span.Contains(r.Span));
            foreach (var reference in references)
            {
                var refDocument = solution.GetDocument(reference.DocumentId);
                var refRoot = refDocument is null ? null : await refDocument.GetSyntaxRootAsync().ConfigureAwait(false);
                var refModel = refDocument is null ? null : await refDocument.GetSemanticModelAsync().ConfigureAwait(false);
                var call = refRoot is null ? null : CallSiteRewriter.CallOf(refRoot.FindToken(reference.Span.Start)) as InvocationExpressionSyntax;
                if (call is null || refModel is null || call.Ancestors().Any(a => a is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax))
                {
                    plan.Uncovered.Add(new { file = reference.File, line = reference.Line, column = reference.Column, reason = Strings.Get("AsyncNotPlainCall") });
                    continue;
                }

                var callText = call.ToString();
                if (suffix && level == 0)
                {
                    var offset = reference.Span.Start - call.SpanStart;
                    callText = callText.Substring(0, offset) + newName + callText.Substring(offset + symbol.Name.Length);
                }

                Add(reference.DocumentId, new TextChange(call.Span, "await " + callText));
                var caller = call.Ancestors().FirstOrDefault(a => a is MethodDeclarationSyntax or LocalFunctionStatementSyntax or ConstructorDeclarationSyntax or AccessorDeclarationSyntax or PropertyDeclarationSyntax);
                if (caller is not MethodDeclarationSyntax callerDeclaration || refModel.GetDeclaredSymbol(callerDeclaration) is not IMethodSymbol callerSymbol)
                {
                    plan.Uncovered.Add(new { file = reference.File, line = reference.Line, column = reference.Column, reason = Strings.Get("AsyncNotPlainCall") });
                    continue;
                }

                if (callerSymbol.IsAsync || seen.Contains(callerSymbol.OriginalDefinition.ToDisplayString()))
                {
                    continue;
                }

                if (level + 1 > depth || !Eligible(callerSymbol))
                {
                    plan.Uncovered.Add(new { file = reference.File, line = reference.Line, column = reference.Column, reason = Strings.Format("AsyncDepthReached", depth) });
                    continue;
                }

                queue.Enqueue((callerSymbol, callerDeclaration, reference.DocumentId, level + 1));
            }
        }

        if (plan.Uncovered.Count > 0)
        {
            plan.Reasons.Add(Strings.Format("MethodEditUncovered", plan.Uncovered.Count));
            return plan;
        }

        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        foreach (var id in changes.Keys)
        {
            edited = await EditChecks.FormatFileAsync(edited, id, plan).ConfigureAwait(false);
        }

        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, changes.Keys, plan).ConfigureAwait(false);
        plan.Files.AddRange(changes.Keys.Select(id => solution.GetDocument(id)!.FilePath ?? id.ToString()));
        plan.Warnings.Add(Strings.Format("AsyncDone", symbol.Name, seen.Count, suffix ? newName : symbol.Name));
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    private static bool Eligible(IMethodSymbol symbol) =>
        !symbol.IsAsync && !symbol.IsOverride && !symbol.IsVirtual && !symbol.IsAbstract && symbol.ContainingType.TypeKind != TypeKind.Interface
        && symbol.ExplicitInterfaceImplementations.Length == 0 && !symbol.IsExtern;

    // "async" goes before the modifiers, and the return type becomes Task or Task<T>. The body is unchanged.
    private static TextChange Header(MethodDeclarationSyntax declaration, IMethodSymbol symbol)
    {
        var returnType = symbol.ReturnsVoid ? "System.Threading.Tasks.Task" : "System.Threading.Tasks.Task<" + symbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + ">";
        var start = declaration.Modifiers.Count > 0 ? declaration.Modifiers[0].SpanStart : declaration.ReturnType.SpanStart;
        var modifiers = string.Concat(declaration.Modifiers.Select(m => m.ValueText + " "));
        return new TextChange(TextSpan.FromBounds(start, declaration.ReturnType.Span.End), "async " + modifiers + returnType);
    }
}
