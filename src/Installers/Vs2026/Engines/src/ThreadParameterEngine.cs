using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Adds one parameter to a method and threads it up its callers (<c>ide_thread_parameter</c>). A caller that already has a parameter of
/// the same type passes that parameter. A caller within <c>depth</c> levels gets the parameter too and passes it on. At the top, a call
/// passes <c>valueAtTop</c>. Every method it changes is listed. Overrides, implementations and <c>params</c> methods are refused.
/// </summary>
internal static class ThreadParameterEngine
{
    public static async Task<MethodEditPlan> PlanAsync(
        Solution solution, string file, int line, int column, string declaration, string valueAtTop, int depth)
    {
        var plan = new MethodEditPlan();
        if (depth < 0)
        {
            plan.Reasons.Add(Strings.Get("ThreadParameterDepth"));
            return plan;
        }

        var target = await EditTarget.LocateAsync(solution, file, line, column, EditTarget.AllMemberKinds, reason => plan.Reasons.Add(reason))
            .ConfigureAwait(false);
        if (target is null)
        {
            return plan;
        }

        if (target.Symbol is not IMethodSymbol method || target.Declaration is not MethodDeclarationSyntax)
        {
            plan.Reasons.Add(Strings.Get("MethodEditNotAMethod"));
            return plan;
        }

        var parsed = SyntaxFactory.ParseParameterList("(" + declaration + ")");
        if (parsed.Parameters.Count != 1 || parsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            plan.Reasons.Add(Strings.Format("ChangeSignatureBadDeclaration", declaration));
            return plan;
        }

        var added = parsed.Parameters[0];
        var addedName = added.Identifier.ValueText;
        var addedType = added.Type?.WithoutTrivia().ToString();

        // Walk the callers breadth first. threaded maps each changed method to its level (0 is the method the user named).
        var threaded = new Dictionary<IMethodSymbol, (MethodDeclarationSyntax Declaration, int Level)>(SymbolEqualityComparer.Default)
        {
            [method] = ((MethodDeclarationSyntax)target.Declaration, 0),
        };
        var queue = new Queue<IMethodSymbol>();
        queue.Enqueue(method);
        var references = new List<MethodEditEngine.Reference>();
        var seen = new HashSet<(DocumentId, int)>();
        var calls = new List<(MethodEditEngine.Reference Reference, InvocationExpressionSyntax Invocation, string Value)>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var level = threaded[current].Level;
            foreach (var reference in await MethodEditEngine.CollectReferencesAsync(solution, current).ConfigureAwait(false))
            {
                if (!seen.Add((reference.DocumentId, reference.Span.Start)))
                {
                    continue;
                }

                references.Add(reference);
                var document = solution.GetDocument(reference.DocumentId);
                var root = document is null ? null : await document.GetSyntaxRootAsync().ConfigureAwait(false);
                var model = document is null ? null : await document.GetSemanticModelAsync().ConfigureAwait(false);
                var call = root is null ? null : CallSiteRewriter.CallOf(root.FindToken(reference.Span.Start));
                if (call is not InvocationExpressionSyntax invocation || model is null)
                {
                    plan.Uncovered.Add(new { file = reference.File, line = reference.Line, column = reference.Column, reason = Strings.Get("CallSiteNotPlainCall") });
                    continue;
                }

                var enclosing = invocation.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                var enclosingSymbol = enclosing is null ? null : model.GetDeclaredSymbol(enclosing) as IMethodSymbol;
                string value;
                var match = enclosing?.ParameterList.Parameters.FirstOrDefault(p => p.Type?.WithoutTrivia().ToString() == addedType);
                if (enclosing is null || enclosingSymbol is null)
                {
                    value = valueAtTop;
                }
                else if (match is not null)
                {
                    value = match.Identifier.ValueText;
                }
                else if (threaded.ContainsKey(enclosingSymbol))
                {
                    value = addedName;
                }
                else if (level + 1 <= depth)
                {
                    threaded[enclosingSymbol] = (enclosing, level + 1);
                    queue.Enqueue(enclosingSymbol);
                    value = addedName;
                }
                else
                {
                    value = valueAtTop;
                }

                calls.Add((reference, invocation, value));
            }
        }

        foreach (var member in threaded.Keys)
        {
            if (member.Parameters.Any(p => p.IsParams))
            {
                plan.Reasons.Add(Strings.Format("ThreadParameterParams", member.ToDisplayString()));
            }
            else if ((await ChangeSignatureFamily.FindAsync(solution, member).ConfigureAwait(false)).Count > 1)
            {
                plan.Reasons.Add(Strings.Format("ThreadParameterFamily", member.ToDisplayString()));
            }
        }

        if (plan.Refused)
        {
            return plan;
        }

        var changes = new Dictionary<DocumentId, List<TextChange>>();
        foreach (var entry in threaded)
        {
            var member = entry.Key;
            var declarationOf = entry.Value.Declaration;
            var documentId = solution.GetDocumentId(declarationOf.SyntaxTree);
            if (documentId is null)
            {
                plan.Reasons.Add(Strings.Format("ThreadParameterFamily", member.ToDisplayString()));
                return plan;
            }

            var parameters = declarationOf.ParameterList.Parameters.Select(p => p.WithoutTrivia().ToString()).Append(declaration);
            var list = SyntaxFactory.ParseParameterList("(" + string.Join(", ", parameters) + ")").ToString();
            if (!changes.TryGetValue(documentId, out var edits))
            {
                changes[documentId] = edits = new List<TextChange>();
            }

            edits.Add(new TextChange(declarationOf.ParameterList.Span, list));
        }

        var callerEdits = new List<CallerEdit>();
        foreach (var (reference, invocation, value) in calls)
        {
            if (invocation.ArgumentList is null)
            {
                continue;
            }

            var arguments = invocation.ArgumentList.Arguments.Select(a => a.ToString()).ToList();
            var named = invocation.ArgumentList.Arguments.Any(a => a.NameColon is not null);
            arguments.Add(named ? addedName + ": " + value : value);
            var text = invocation.ToString();
            var start = invocation.ArgumentList.SpanStart - invocation.SpanStart;
            var end = invocation.ArgumentList.Span.End - invocation.SpanStart;
            var callText = text.Substring(0, start) + "(" + string.Join(", ", arguments) + ")" + text.Substring(end);
            callerEdits.Add(new CallerEdit(reference.File, reference.Line, reference.Column, callText));
        }

        if (plan.Uncovered.Count > 0)
        {
            plan.Reasons.Add(Strings.Format("MethodEditUncovered", plan.Uncovered.Count));
            return plan;
        }

        if (!await MethodEditEngine.AddCallerEditsAsync(solution, references, callerEdits, changes, plan).ConfigureAwait(false))
        {
            return plan;
        }

        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        var primary = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault();
        if (primary is not null)
        {
            edited = await EditChecks.FormatFileAsync(edited, primary, plan).ConfigureAwait(false);
        }

        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, changes.Keys.Concat(references.Select(r => r.DocumentId)), plan)
            .ConfigureAwait(false);

        foreach (var id in changes.Keys)
        {
            plan.Files.Add(solution.GetDocument(id)!.FilePath ?? id.ToString());
        }

        plan.SignatureChanged = true;
        plan.ReferenceCount = references.Count;
        plan.Tests.AddRange(await TestDiscovery.FindDirectAsync(solution, references).ConfigureAwait(false));
        var others = threaded.Keys.Where(k => !SymbolEqualityComparer.Default.Equals(k, method)).Select(k => k.ToDisplayString()).ToList();
        if (others.Count > 0)
        {
            plan.Warnings.Add(Strings.Format("ThreadParameterChanged", string.Join(", ", others)));
        }

        if (threaded.Values.Any(v => HasDocComment(v.Declaration)))
        {
            plan.Warnings.Add(Strings.Get("ThreadParameterDocs"));
        }

        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    private static bool HasDocComment(SyntaxNode declaration) =>
        declaration.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
}
