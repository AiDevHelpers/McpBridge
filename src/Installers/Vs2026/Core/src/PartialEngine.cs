using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Merges the parts of a partial type into the part at the line (<c>ide_partial</c>, action merge). Each other part's members move to the
/// end of that part, its usings that the part's file lacks are added, and its declaration is removed. A part that was alone in its file
/// takes the file with it. Refused when the type is not partial, or has one part. Pure: the caller applies <see cref="MethodEditPlan.Result"/>.
/// </summary>
internal static class PartialEngine
{
    public static async Task<MethodEditPlan> MergeAsync(Solution solution, string file, int line)
    {
        var plan = new MethodEditPlan();
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
        var primary = root!.FindToken(Math.Min(position, root.FullSpan.End)).Parent?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (primary is null || model!.GetDeclaredSymbol(primary) is not INamedTypeSymbol symbol)
        {
            plan.Reasons.Add(Strings.Get("EditTypeNotAType"));
            return plan;
        }

        var parts = symbol.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<TypeDeclarationSyntax>().ToList();
        if (!primary.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)) || parts.Count < 2)
        {
            plan.Reasons.Add(Strings.Get("PartialNothingToMerge"));
            return plan;
        }

        var eol = text.ToString().Contains("\r\n") ? "\r\n" : "\n";
        var changes = new Dictionary<DocumentId, List<TextChange>>();
        void Add(DocumentId id, TextChange change)
        {
            if (!changes.TryGetValue(id, out var list))
            {
                changes[id] = list = new List<TextChange>();
            }

            list.Add(change);
        }

        // Members of the other parts, in order, go to the end of the primary part.
        var moved = new List<string>();
        var addedUsings = new List<string>();
        var primaryUsings = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => u.WithoutTrivia().ToString()).ToHashSet(StringComparer.Ordinal);
        var primaryId = documentId!;
        foreach (var part in parts.Where(p => !ReferenceEquals(p, primary)))
        {
            var partId = solution.GetDocumentId(part.SyntaxTree);
            if (partId is null)
            {
                plan.Reasons.Add(Strings.Get("PartialNothingToMerge"));
                return plan;
            }

            var partText = await solution.GetDocument(partId)!.GetTextAsync().ConfigureAwait(false);
            var inner = part.CloseBraceToken.SpanStart > part.OpenBraceToken.Span.End
                ? partText.GetSubText(TextSpan.FromBounds(part.OpenBraceToken.Span.End, part.CloseBraceToken.SpanStart)).ToString()
                : "";
            var innerLines = inner.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
            while (innerLines.Count > 0 && innerLines[0].Trim().Length == 0)
            {
                innerLines.RemoveAt(0);
            }

            while (innerLines.Count > 0 && innerLines[innerLines.Count - 1].Trim().Length == 0)
            {
                innerLines.RemoveAt(innerLines.Count - 1);
            }

            if (innerLines.Count > 0)
            {
                moved.Add(string.Join(eol, innerLines));
            }

            var partRoot = await solution.GetDocument(partId)!.GetSyntaxRootAsync().ConfigureAwait(false);
            foreach (var use in partRoot!.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                var text2 = use.WithoutTrivia().ToString();
                if (primaryUsings.Add(text2))
                {
                    addedUsings.Add(text2);
                }
            }

            var alone = partRoot.DescendantNodes().OfType<TypeDeclarationSyntax>().Count() == 1;
            var partPath = solution.GetDocument(partId)!.FilePath ?? "";
            if (alone)
            {
                plan.FileDeletes.Add(partPath);
            }
            else
            {
                Add(partId, new TextChange(part.FullSpan, ""));
            }
        }

        if (moved.Count > 0)
        {
            var indent = primary.Members.Count > 0
                ? MethodAddEngine.LeadingWhitespace(text.Lines.GetLineFromPosition(primary.Members[0].SpanStart).ToString())
                : MethodAddEngine.LeadingWhitespace(text.Lines.GetLineFromPosition(primary.CloseBraceToken.SpanStart).ToString()) + "    ";
            var block = string.Join(eol + eol, moved.Select(m => string.Join(eol, m.Split(new[] { eol }, StringSplitOptions.None).Select(l => l.Length == 0 ? "" : indent + l))));
            Add(primaryId, new TextChange(new TextSpan(primary.CloseBraceToken.SpanStart, 0), block + eol));
        }

        if (addedUsings.Count > 0)
        {
            Add(primaryId, new TextChange(new TextSpan(0, 0), string.Join(eol, addedUsings) + eol + eol));
        }

        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        foreach (var id in changes.Keys)
        {
            edited = await EditChecks.FormatFileAsync(edited, id, plan).ConfigureAwait(false);
        }

        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, changes.Keys, plan).ConfigureAwait(false);
        plan.Files.AddRange(changes.Keys.Select(id => solution.GetDocument(id)!.FilePath ?? id.ToString()));
        plan.SignatureChanged = false;
        plan.Warnings.Add(Strings.Format("PartialMerged", symbol.Name, parts.Count - 1));
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }
}
