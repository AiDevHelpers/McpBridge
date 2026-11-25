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
/// Plans adding a method to a type, or adding an overload of an existing method. The new method gets a doc comment in the layout of
/// its neighbours when they have one (text comes from the caller, never invented). An overload is refused if it changes which method
/// any existing call binds to. Pure: it changes nothing; the caller applies <see cref="MethodEditPlan.Result"/>.
/// </summary>
internal static class MethodAddEngine
{
    public static async Task<MethodEditPlan> PlanAsync(
        Solution solution, string file, int line, string newMethodText, int? afterLine, bool overload, DocUpdates docs, Solution? baseline = null)
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

        var parsed = SyntaxFactory.ParseMemberDeclaration(newMethodText) as MethodDeclarationSyntax;
        if (parsed is null || parsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            plan.Reasons.Add(Strings.Get("MethodEditNewTextNotOneMethod"));
            return plan;
        }

        if (parsed.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)))
        {
            plan.Reasons.Add(Strings.Get("MethodEditNewTextHasDocs"));
            return plan;
        }

        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        var lineStart = text.Lines[line - 1].Start;
        var position = lineStart + (text.Lines[line - 1].ToString().Length - text.Lines[line - 1].ToString().TrimStart().Length);
        var at = root!.FindToken(Math.Min(position, root.FullSpan.End)).Parent;

        MethodDeclarationSyntax? existing = null;
        IMethodSymbol? existingSymbol = null;
        if (overload)
        {
            existing = at?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            existingSymbol = existing is null ? null : model!.GetDeclaredSymbol(existing);
            if (existing is null || existingSymbol is null)
            {
                plan.Reasons.Add(Strings.Get("MethodEditNotAMethod"));
                return plan;
            }

            if (parsed.Identifier.ValueText != existing.Identifier.ValueText)
            {
                plan.Reasons.Add(Strings.Format("MethodAddOverloadNameDiffers", existing.Identifier.ValueText));
                return plan;
            }
        }

        var type = at?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (type is null)
        {
            plan.Reasons.Add(Strings.Get("MethodAddNoType"));
            return plan;
        }

        var eol = text.ToString().Contains("\r\n") ? "\r\n" : "\n";
        var (insertAt, before, suffix, memberIndent) = FindInsertion(text, type, existing, afterLine, eol, plan);
        if (plan.Refused)
        {
            return plan;
        }

        // Phase 1: the method alone, so its symbol exists and its docs can follow its parameters.
        var withoutDocs = BuildBlock(null, newMethodText, memberIndent, eol);
        var phaseOne = solution.WithDocumentText(documentId!, text.WithChanges(new TextChange(new TextSpan(insertAt, 0), before + withoutDocs + suffix)));
        var newSymbol = await FindNewMethodAsync(phaseOne, documentId!, insertAt, parsed).ConfigureAwait(false);
        if (newSymbol is null)
        {
            plan.Reasons.Add(Strings.Get("MethodEditNewMethodNotFound"));
            return plan;
        }

        plan.NewSignature = newSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        plan.AddedIsTest = TestDiscovery.IsTestMethod(newSymbol);

        // Phase 2: with a doc comment, when the neighbours have one.
        var docLines = new List<string>();
        var donor = overload && existing is not null && HasDocs(existing)
            ? existing
            : type.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(HasDocs);
        if (donor is not null)
        {
            var result = overload && existing is not null && ReferenceEquals(donor, existing)
                ? MethodDocUpdater.Update(existing, existingSymbol!, newSymbol, docs, true, eol)
                : MethodDocUpdater.BuildNew(donor, newSymbol, docs);
            plan.Warnings.AddRange(result.Warnings);
            if (result.Missing.Count > 0)
            {
                plan.Warnings.Add(Strings.Format("MethodAddDocsNotGiven", string.Join(", ", result.Missing)));
            }
            else
            {
                docLines = result.Lines;
            }
        }

        var block = BuildBlock(docLines, newMethodText, memberIndent, eol);
        var edited = solution.WithDocumentText(documentId!, text.WithChanges(new TextChange(new TextSpan(insertAt, 0), before + block + suffix)));
        plan.DocsUpdated = docLines.Count > 0;
        plan.Files.Add(document.FilePath ?? file);
        plan.InsertedAtLine = text.Lines.GetLineFromPosition(Math.Min(insertAt, text.Length)).LineNumber + 1 + CountLines(before) + docLines.Count;

        if (overload)
        {
            // The tests that call the original are the nearest tests the overload has.
            var originalReferences = await MethodEditEngine.CollectReferencesAsync(solution, existingSymbol!).ConfigureAwait(false);
            plan.Tests.AddRange(await TestDiscovery.FindDirectAsync(solution, originalReferences).ConfigureAwait(false));
            await CheckRebindsAsync(solution, edited, existingSymbol!, documentId!, insertAt, (before + block + suffix).Length, plan).ConfigureAwait(false);
        }

        edited = await EditChecks.FormatFileAsync(edited, documentId!, plan).ConfigureAwait(false);
        await EditChecks.CheckNewDiagnosticsAsync(baseline ?? solution, edited, new[] { documentId! }, plan).ConfigureAwait(false);
        plan.Method = await TestDiscovery.LocateAsync(edited, documentId!, TestDiscovery.MetadataName(newSymbol.ContainingType), MethodEditEngine.SignatureKey(newSymbol))
            .ConfigureAwait(false);
        plan.Added = TestDiscovery.TargetFor(newSymbol, document, plan.InsertedAtLine);

        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    internal static int CountLines(string text) => text.Count(c => c == '\n');

    private static bool HasDocs(MethodDeclarationSyntax method) =>
        method.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia));

    // Where the new text goes: after the last overload, after the member at afterLine, or at the end of the type.
    private static (int At, string Before, string Suffix, string Indent) FindInsertion(
        SourceText text, TypeDeclarationSyntax type, MethodDeclarationSyntax? existing, int? afterLine, string eol, MethodEditPlan plan)
    {
        var indent = type.Members.Count > 0
            ? LeadingWhitespace(text.Lines.GetLineFromPosition(type.Members[0].SpanStart).ToString())
            : LeadingWhitespace(text.Lines.GetLineFromPosition(type.CloseBraceToken.SpanStart).ToString()) + "    ";

        MemberDeclarationSyntax? after = null;
        if (existing is not null)
        {
            after = type.Members.OfType<MethodDeclarationSyntax>().Last(m => m.Identifier.ValueText == existing.Identifier.ValueText);
        }
        else if (afterLine is { } l)
        {
            after = type.Members.FirstOrDefault(m =>
                text.Lines.GetLineFromPosition(m.SpanStart).LineNumber + 1 <= l && l <= text.Lines.GetLineFromPosition(m.Span.End).LineNumber + 1);
            if (after is null)
            {
                plan.Reasons.Add(Strings.Format("MethodAddAfterLineNotMember", l));
                return (0, "", "", indent);
            }
        }

        if (after is not null)
        {
            // At the end of the previous member's last line: that line's own newline follows the new block.
            return (text.Lines.GetLineFromPosition(after.Span.End).End, eol + eol, "", indent);
        }

        var brace = type.CloseBraceToken;
        var braceLine = text.Lines.GetLineFromPosition(brace.SpanStart);
        var braceFirst = braceLine.ToString().TrimStart().StartsWith("}", StringComparison.Ordinal);
        if (braceFirst)
        {
            return (braceLine.Start, type.Members.Count > 0 ? eol : "", eol, indent);
        }

        return (brace.SpanStart, eol, eol, indent);
    }

    // The lines of an optional doc comment and the method, each indented. A leading eol in "before" makes the blank line.
    private static string BuildBlock(IEnumerable<string>? docLines, string methodText, string indent, string eol)
    {
        var lines = (docLines ?? Enumerable.Empty<string>()).Concat(methodText.Replace("\r\n", "\n").Split('\n'));
        return string.Join(eol, lines.Select(l => l.Length == 0 ? "" : indent + l));
    }

    internal static string LeadingWhitespace(string line) => new string(line.TakeWhile(c => c == ' ' || c == '\t').ToArray());

    private static async Task<IMethodSymbol?> FindNewMethodAsync(Solution edited, DocumentId id, int insertAt, MethodDeclarationSyntax parsed)
    {
        var document = edited.GetDocument(id)!;
        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        var node = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m =>
            m.SpanStart >= insertAt && m.Identifier.ValueText == parsed.Identifier.ValueText
            && m.ParameterList.ToString() == parsed.ParameterList.ToString());
        return node is null || model is null ? null : model.GetDeclaredSymbol(node);
    }

    // Adding an overload can move an existing call to the new method. Each call that used to bind to the original must still bind to it.
    private static async Task CheckRebindsAsync(
        Solution before, Solution after, IMethodSymbol original, DocumentId editedId, int insertAt, int insertedLength, MethodEditPlan plan)
    {
        var originalSignature = original.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        foreach (var referenced in await SymbolFinder.FindReferencesAsync(original, before).ConfigureAwait(false))
        {
            foreach (var reference in referenced.Locations)
            {
                var id = reference.Document.Id;
                var position = reference.Location.SourceSpan.Start + (id == editedId && reference.Location.SourceSpan.Start >= insertAt ? insertedLength : 0);
                var document = after.GetDocument(id)!;
                var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
                var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
                var token = root!.FindToken(Math.Min(position, root.FullSpan.End));
                var node = MethodEditEngine.UseNode(token);
                var info = model!.GetSymbolInfo(node);
                var bound = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                var now = bound?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                if (now is not null && now != originalSignature)
                {
                    var span = reference.Location.GetLineSpan();
                    plan.Rebinds.Add(new { file = span.Path, line = span.StartLinePosition.Line + 1, column = span.StartLinePosition.Character + 1, was = originalSignature, now });
                }
            }
        }

        if (plan.Rebinds.Count > 0)
        {
            plan.Reasons.Add(Strings.Format("MethodAddRebinds", plan.Rebinds.Count));
        }
    }
}
