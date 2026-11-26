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
/// Moves a method, property or field to another type in the solution (<c>ide_move_member</c>, unit 5.1). The member's text, doc comment
/// included, is removed from its type and written at the end of the destination. References qualified by the source type's name
/// (<c>Source.Member</c>) are rewritten to the destination's name; every other reference needs a caller edit, as with a delete. Anything
/// that stops compiling in the new place (unqualified use of the source type's other members, for example) is refused by the checks.
/// Pure: the caller applies <see cref="MethodEditPlan.Result"/>.
/// </summary>
internal static class MoveMemberEngine
{
    public static async Task<MethodEditPlan> PlanAsync(Solution solution, string file, int line, int column, string toType, IReadOnlyList<CallerEdit> callerEdits)
    {
        var plan = new MethodEditPlan();
        var target = await EditTarget.LocateAsync(solution, file, line, column,
            new[] { typeof(MethodDeclarationSyntax), typeof(PropertyDeclarationSyntax), typeof(FieldDeclarationSyntax) },
            r => plan.Reasons.Add(r)).ConfigureAwait(false);
        if (target is null)
        {
            return plan;
        }

        var symbol = target.Symbol;
        var source = symbol.ContainingType;
        var destination = await FindTypeAsync(solution, toType, plan).ConfigureAwait(false);
        if (destination is null)
        {
            return plan;
        }

        if (SymbolEqualityComparer.Default.Equals(destination, source))
        {
            plan.Reasons.Add(Strings.Get("MoveSameType"));
            return plan;
        }

        var related = await CoverageOfReferences.RelatedAsync(solution, symbol).ConfigureAwait(false);
        if (related.Count > 0)
        {
            plan.Reasons.Add(Strings.Format("MoveRelated", string.Join(", ", related)));
            return plan;
        }

        var destinationDeclaration = destination.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<TypeDeclarationSyntax>().FirstOrDefault();
        var destinationId = destinationDeclaration is null ? null : solution.GetDocumentId(destinationDeclaration.SyntaxTree);
        if (destinationDeclaration is null || destinationId is null)
        {
            plan.Reasons.Add(Strings.Format("MoveNoDestination", toType));
            return plan;
        }

        var references = await MethodEditEngine.CollectReferencesAsync(solution, symbol).ConfigureAwait(false);
        var declaration = (MemberDeclarationSyntax)target.Declaration;
        var inside = references.Where(r => r.DocumentId == target.DocumentId && declaration.Span.Contains(r.Span)).ToList();
        references.RemoveAll(r => inside.Contains(r));
        plan.ReferenceCount = references.Count;

        // A qualified use is rewritten to the destination's name; anything else must be covered by a caller edit.
        var changes = new Dictionary<DocumentId, List<TextChange>>();
        void Add(DocumentId id, TextChange change)
        {
            if (!changes.TryGetValue(id, out var list))
            {
                changes[id] = list = new List<TextChange>();
            }

            list.Add(change);
        }

        var unqualified = new List<MethodEditEngine.Reference>();
        foreach (var reference in references)
        {
            var document = solution.GetDocument(reference.DocumentId);
            var root = document is null ? null : await document.GetSyntaxRootAsync().ConfigureAwait(false);
            var token = root?.FindToken(reference.Span.Start);
            var access = token?.Parent?.Parent as MemberAccessExpressionSyntax;
            if (access is { Expression: IdentifierNameSyntax qualifier } && qualifier.Identifier.ValueText == source.Name)
            {
                Add(reference.DocumentId, new TextChange(qualifier.Span, destination.Name));
            }
            else
            {
                unqualified.Add(reference);
            }
        }

        var uncovered = CoverageOfReferences.Uncovered(unqualified, callerEdits);
        if (uncovered.Count > 0)
        {
            plan.Uncovered.AddRange(uncovered);
            plan.Reasons.Add(Strings.Format("MoveUncovered", uncovered.Count));
            return plan;
        }

        if (!await MethodEditEngine.AddCallerEditsAsync(solution, unqualified, callerEdits, changes, plan).ConfigureAwait(false))
        {
            return plan;
        }

        // The member's text, doc comment and attributes included, written at the end of the destination with its indentation.
        var movedText = declaration.ToFullString().Trim().Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        var destinationText = await solution.GetDocument(destinationId)!.GetTextAsync().ConfigureAwait(false);
        var eol = destinationText.ToString().Contains("\r\n") ? "\r\n" : "\n";
        var destinationType = destinationDeclaration;
        var indent = destinationType.Members.Count > 0
            ? MethodAddEngine.LeadingWhitespace(destinationText.Lines.GetLineFromPosition(destinationType.Members[0].SpanStart).ToString())
            : MethodAddEngine.LeadingWhitespace(destinationText.Lines.GetLineFromPosition(destinationType.CloseBraceToken.SpanStart).ToString()) + "    ";
        var block = string.Join(eol, movedText.Select(l => l.Length == 0 ? "" : indent + l));
        var last = destinationType.Members.LastOrDefault();
        Add(destinationId, last is null
            ? new TextChange(new TextSpan(destinationType.OpenBraceToken.Span.End, 0), eol + block)
            : new TextChange(new TextSpan(last.Span.End, 0), eol + eol + block));

        // The member leaves its type: its declaration, doc comment included, is removed from the source document.
        Add(target.DocumentId, new TextChange(declaration.FullSpan, ""));

        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        foreach (var id in changes.Keys)
        {
            edited = await EditChecks.FormatFileAsync(edited, id, plan).ConfigureAwait(false);
        }

        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, changes.Keys.Concat(references.Select(r => r.DocumentId)), plan).ConfigureAwait(false);
        plan.Files.AddRange(changes.Keys.Select(id => solution.GetDocument(id)!.FilePath ?? id.ToString()));
        plan.Tests.AddRange(await TestDiscovery.FindDirectAsync(solution, references).ConfigureAwait(false));
        plan.Warnings.Add(Strings.Format("MoveDone", symbol.Name, source.Name, destination.Name));
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    // The one source type with this name, or with this display name (for a namespace-qualified name). Several matches are refused.
    private static async Task<INamedTypeSymbol?> FindTypeAsync(Solution solution, string name, MethodEditPlan plan)
    {
        var matches = new List<INamedTypeSymbol>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            foreach (var type in compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type).OfType<INamedTypeSymbol>())
            {
                if (type.Locations.Any(l => l.IsInSource) && (type.Name == name || type.ToDisplayString() == name))
                {
                    matches.Add(type);
                }
            }
        }

        var distinct = matches.Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>().ToList();
        if (distinct.Count == 1)
        {
            return distinct[0];
        }

        plan.Reasons.Add(distinct.Count == 0 ? Strings.Format("MoveNoDestination", name) : Strings.Format("MoveAmbiguous", name));
        return null;
    }
}
