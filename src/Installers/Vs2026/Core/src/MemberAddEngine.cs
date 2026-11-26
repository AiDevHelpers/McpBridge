using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Plans adding fields and properties to a type: the caller says each member's type, privacy and backing, and the engine renders it
/// in the type's own layout, with a doc comment in the neighbours' style when they have one. Pure: it changes nothing; the caller
/// applies <see cref="MethodEditPlan.Result"/>.
/// </summary>
internal static class MemberAddEngine
{
    /// <summary>
    /// When <paramref name="finalize"/> is false the members are only inserted (no format, no diagnostics): the method tools use that
    /// to add members first and check everything once at the end.
    /// </summary>
    public static async Task<MethodEditPlan> PlanAsync(Solution solution, string file, int line, IReadOnlyList<MemberSpec> specs, bool finalize)
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
        var lineText = text.Lines[line - 1].ToString();
        var position = text.Lines[line - 1].Start + (lineText.Length - lineText.TrimStart().Length);
        var type = root!.FindToken(Math.Min(position, root.FullSpan.End)).Parent?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (type is null)
        {
            plan.Reasons.Add(Strings.Get("MethodAddNoType"));
            return plan;
        }

        if (specs.Count == 0)
        {
            plan.Reasons.Add(Strings.Get("MemberNoneGiven"));
            return plan;
        }

        var existing = new HashSet<string>(type.Members.SelectMany(NamesOf), StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            var problem = MemberRenderer.Validate(spec);
            if (problem is not null)
            {
                plan.Reasons.Add(problem);
                continue;
            }

            var names = spec.Declaration is not null ? MemberRenderer.DeclaredNames(spec.Declaration)
                : spec.Backing == "field" ? new[] { spec.Name, spec.BackingFieldName } : new[] { spec.Name };
            foreach (var name in names)
            {
                if (!existing.Add(name))
                {
                    plan.Reasons.Add(Strings.Format("MemberExists", name));
                }
            }
        }

        if (plan.Refused)
        {
            return plan;
        }

        var eol = text.ToString().Contains("\r\n") ? "\r\n" : "\n";
        var donor = type.Members.FirstOrDefault(m => m.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)));
        var lines = new List<string>();
        foreach (var spec in specs)
        {
            var rendered = MemberRenderer.Render(spec);
            var docs = DocLines(donor, spec.Summary);
            if (donor is not null && docs.Count == 0)
            {
                plan.Warnings.Add(Strings.Format("MemberDocsNotGiven", spec.Name));
            }

            if (lines.Count > 0 && (docs.Count > 0 || rendered.Count > 1 || spec.Backing is "auto" or "field" or "expression"))
            {
                lines.Add("");
            }

            lines.AddRange(docs);
            lines.AddRange(rendered);
        }

        if (MemberRenderer.ValidateRendered(lines) is { } invalid)
        {
            plan.Reasons.Add(invalid);
            return plan;
        }

        var (insertAt, before, suffix, indent) = FindInsertion(text, type, specs, eol, plan);
        if (plan.Refused)
        {
            return plan;
        }

        var block = string.Join(eol, lines.Select(l => l.Length == 0 ? "" : indent + l));
        var inserted = before + block + suffix;
        var edited = solution.WithDocumentText(documentId!, text.WithChanges(new TextChange(new TextSpan(insertAt, 0), inserted)));
        plan.Files.Add(document.FilePath ?? file);
        plan.InsertedAtLine = text.Lines.GetLineFromPosition(Math.Min(insertAt, text.Length)).LineNumber + 1 + MethodAddEngine.CountLines(before);
        plan.InsertedLines = MethodAddEngine.CountLines(inserted);
        plan.InsertedAfterLine = text.Lines.GetLineFromPosition(Math.Min(insertAt, text.Length)).LineNumber + 1;
        plan.NewSignature = string.Join(", ", specs.Select(s => s.Declaration is not null ? s.Declaration.Trim() : s.Accessibility + " " + s.Type + " " + s.Name));

        if (finalize)
        {
            edited = await EditChecks.FormatFileAsync(edited, documentId!, plan).ConfigureAwait(false);
            await EditChecks.CheckNewDiagnosticsAsync(solution, edited, new[] { documentId! }, plan).ConfigureAwait(false);
        }

        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    internal static IEnumerable<string> NamesOf(MemberDeclarationSyntax member) => member switch
    {
        PropertyDeclarationSyntax p => new[] { p.Identifier.ValueText },
        FieldDeclarationSyntax f => f.Declaration.Variables.Select(v => v.Identifier.ValueText),
        MethodDeclarationSyntax m => new[] { m.Identifier.ValueText },
        EventFieldDeclarationSyntax e => e.Declaration.Variables.Select(v => v.Identifier.ValueText),
        _ => Array.Empty<string>(),
    };

    // A summary in the layout a documented neighbour uses. Empty when no neighbour has docs or the caller gave no text.
    private static List<string> DocLines(MemberDeclarationSyntax? donor, string? summary)
    {
        var lines = new List<string>();
        var trivia = donor?.GetLeadingTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia));
        if (donor is null || trivia is not { } found || found == default || string.IsNullOrWhiteSpace(summary))
        {
            return lines;
        }

        var raw = found.ToFullString();
        var prefix = raw.Length > 3 && raw[3] == ' ' ? "/// " : "///";
        if (Regex.IsMatch(raw, @"<summary>\s*\r?\n"))
        {
            lines.Add(prefix + "<summary>");
            lines.AddRange(summary!.Replace("\r\n", "\n").Trim().Split('\n').Select(l => prefix + l.TrimEnd()));
            lines.Add(prefix + "</summary>");
        }
        else
        {
            lines.Add(prefix + "<summary>" + summary!.Trim() + "</summary>");
        }

        return lines;
    }

    // After the last field or property; else just inside the opening brace; else at the end of an empty type.
    private static (int At, string Before, string Suffix, string Indent) FindInsertion(
        SourceText text, TypeDeclarationSyntax type, IReadOnlyList<MemberSpec> specs, string eol, MethodEditPlan plan)
    {
        var last = type.Members.LastOrDefault(m => m is FieldDeclarationSyntax or PropertyDeclarationSyntax);
        var indent = type.Members.Count > 0
            ? MethodAddEngine.LeadingWhitespace(text.Lines.GetLineFromPosition((last ?? type.Members[0]).SpanStart).ToString())
            : MethodAddEngine.LeadingWhitespace(text.Lines.GetLineFromPosition(type.CloseBraceToken.SpanStart).ToString()) + "    ";

        if (last is not null)
        {
            var plainFields = specs[0].Backing is "readonly-field" or "field-only";
            var blank = !(last is FieldDeclarationSyntax && plainFields);
            return (text.Lines.GetLineFromPosition(last.Span.End).End, blank ? eol + eol : eol, "", indent);
        }

        if (type.Members.Count > 0)
        {
            return (text.Lines.GetLineFromPosition(type.OpenBraceToken.Span.End).End, eol, eol, indent);
        }

        var brace = type.CloseBraceToken;
        var braceLine = text.Lines.GetLineFromPosition(brace.SpanStart);
        return braceLine.ToString().TrimStart().StartsWith("}", StringComparison.Ordinal)
            ? (braceLine.Start, "", eol, indent)
            : (brace.SpanStart, eol, eol, indent);
    }
}
