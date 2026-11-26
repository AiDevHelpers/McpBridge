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
/// The members whose signature must change together with a method: the base methods it overrides, the overrides of any of them, and
/// the interface members each one implements and their implementations. A change to one of them without the rest breaks compilation,
/// so <c>ide_change_signature</c> changes the whole family in one plan. Only the parameter list changes on the other members; their
/// return types, accessibility and modifiers stay as they are, and their doc comments are not updated (the plan says so).
/// </summary>
internal static class ChangeSignatureFamily
{
    public static async Task<List<IMethodSymbol>> FindAsync(Solution solution, IMethodSymbol method)
    {
        var family = new List<IMethodSymbol>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<IMethodSymbol>();
        queue.Enqueue(method);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            family.Add(current);
            if (current.OverriddenMethod is { } overridden)
            {
                queue.Enqueue(overridden);
            }

            foreach (var iface in current.ContainingType.AllInterfaces)
            {
                foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
                {
                    if (SymbolEqualityComparer.Default.Equals(current.ContainingType.FindImplementationForInterfaceMember(member), current))
                    {
                        queue.Enqueue(member);
                    }
                }
            }

            foreach (var overrider in await SymbolFinder.FindOverridesAsync(current, solution).ConfigureAwait(false))
            {
                if (overrider is IMethodSymbol m)
                {
                    queue.Enqueue(m);
                }
            }

            foreach (var implementation in await SymbolFinder.FindImplementationsAsync(current, solution).ConfigureAwait(false))
            {
                if (implementation is IMethodSymbol m)
                {
                    queue.Enqueue(m);
                }
            }
        }

        return family;
    }

    public static async Task<MethodEditPlan> PlanAsync(
        Solution solution, string file, int line, int column, IReadOnlyList<NewParameter> newParameters, IReadOnlyList<IMethodSymbol> family, DocUpdates? docs)
    {
        var plan = new MethodEditPlan();
        var declarations = new List<(IMethodSymbol Member, MethodDeclarationSyntax Declaration)>();
        foreach (var member in family)
        {
            var declaration = member.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (declaration is null || !member.Locations.Any(l => l.IsInSource))
            {
                plan.Reasons.Add(Strings.Format("ChangeSignatureRelatedOutside", member.ToDisplayString()));
                return plan;
            }

            declarations.Add((member, declaration));
        }

        var parameterList = SyntaxFactory.ParseParameterList("(" + string.Join(", ", newParameters.Select(p => p.Declaration)) + ")").ToString();
        var changes = new Dictionary<DocumentId, List<TextChange>>();
        var references = new List<MethodEditEngine.Reference>();
        var seen = new HashSet<(DocumentId, int)>();
        var calls = new List<CallerEdit>();
        foreach (var (member, declaration) in declarations)
        {
            var documentId = solution.GetDocumentId(declaration.SyntaxTree);
            if (documentId is null)
            {
                plan.Reasons.Add(Strings.Format("ChangeSignatureRelatedOutside", member.ToDisplayString()));
                return plan;
            }

            if (!changes.TryGetValue(documentId, out var list))
            {
                changes[documentId] = list = new List<TextChange>();
            }

            list.Add(new TextChange(declaration.ParameterList.Span, parameterList));

            var all = await MethodEditEngine.CollectReferencesAsync(solution, member).ConfigureAwait(false);
            var fresh = all.Where(r => seen.Add((r.DocumentId, r.Span.Start))).ToList();
            references.AddRange(fresh);
            var (edits, uncovered) = await CallSiteRewriter.RewriteAsync(solution, member, fresh, newParameters).ConfigureAwait(false);
            plan.Uncovered.AddRange(uncovered);
            calls.AddRange(edits);
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
        plan.Warnings.Add(Strings.Format("ChangeSignatureFamily", string.Join(", ", declarations.Select(d => d.Member.ToDisplayString()))));
        if ((docs is not null && (docs.NewSummary is not null || docs.Items.Count > 0)) || declarations.Any(d => HasDocComment(d.Declaration)))
        {
            plan.Warnings.Add(Strings.Get("ChangeSignatureFamilyDocs"));
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
