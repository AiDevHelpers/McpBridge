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
/// Changes a class, struct or record's modifiers and base list (<c>ide_edit_type</c>). The modifiers replace the whole set and apply to
/// every part of a partial type; the base list applies to the part at the line. Adding abstract or static is refused while the type is
/// used, since code that creates it or derives from it would stop compiling. Narrowing access is checked by the edit checks against each
/// use. Pure: the caller applies <see cref="MethodEditPlan.Result"/>.
/// </summary>
internal static class EditTypeEngine
{
    private static readonly string[] AllowedModifiers =
    {
        "public", "internal", "private", "protected", "static", "abstract", "sealed", "partial", "readonly", "unsafe", "ref",
    };

    public static async Task<MethodEditPlan> PlanAsync(Solution solution, string file, int line, string? modifiers, IReadOnlyList<string>? baseList)
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
        var part = root!.FindToken(Math.Min(position, root.FullSpan.End)).Parent?.AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (part is null || model!.GetDeclaredSymbol(part) is not INamedTypeSymbol symbol)
        {
            plan.Reasons.Add(Strings.Get("EditTypeNotAType"));
            return plan;
        }

        var words = (modifiers ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var newModifiers = modifiers is null ? null : words;
        if (newModifiers is not null && newModifiers.Any(w => !AllowedModifiers.Contains(w)))
        {
            plan.Reasons.Add(Strings.Format("EditTypeBadModifier", string.Join(" ", newModifiers.Where(w => !AllowedModifiers.Contains(w)))));
            return plan;
        }

        var parts = symbol.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<TypeDeclarationSyntax>().ToList();
        if (newModifiers is not null && parts.Count > 1 && !newModifiers.Contains("partial"))
        {
            plan.Reasons.Add(Strings.Get("EditTypePartialNeedsPartial"));
            return plan;
        }

        var references = await MethodEditEngine.CollectReferencesAsync(solution, symbol).ConfigureAwait(false);
        references.RemoveAll(r => parts.Any(p => solution.GetDocumentId(p.SyntaxTree) == r.DocumentId && p.Span.Contains(r.Span)));
        plan.ReferenceCount = references.Count;

        var oldModifiers = part.Modifiers.Select(m => m.ValueText).ToList();
        var added = (newModifiers ?? oldModifiers.ToArray()).Except(oldModifiers).ToList();
        if (added.Any(w => w is "abstract" or "static") && references.Count > 0)
        {
            plan.Reasons.Add(Strings.Format("EditTypeInUse", string.Join(" ", added.Where(w => w is "abstract" or "static")), references.Count));
            return plan;
        }

        var changes = new Dictionary<DocumentId, List<TextChange>>();
        foreach (var declaration in parts)
        {
            var declarationDocument = solution.GetDocumentId(declaration.SyntaxTree);
            if (declarationDocument is null)
            {
                plan.Reasons.Add(Strings.Get("EditTypeNotAType"));
                return plan;
            }

            var bases = ReferenceEquals(declaration, part) && baseList is not null ? baseList : null;
            if (newModifiers is null && bases is null)
            {
                continue;
            }

            if (!changes.TryGetValue(declarationDocument, out var list))
            {
                changes[declarationDocument] = list = new List<TextChange>();
            }

            list.Add(HeaderChange(declaration, newModifiers ?? declaration.Modifiers.Select(m => m.ValueText), bases));
        }

        if (changes.Count == 0)
        {
            plan.Reasons.Add(Strings.Get("EditTypeNothingToDo"));
            return plan;
        }

        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        foreach (var id in changes.Keys)
        {
            edited = await EditChecks.FormatFileAsync(edited, id, plan).ConfigureAwait(false);
        }

        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, changes.Keys.Concat(references.Select(r => r.DocumentId)), plan).ConfigureAwait(false);
        foreach (var id in changes.Keys)
        {
            plan.Files.Add(solution.GetDocument(id)!.FilePath ?? id.ToString());
        }

        plan.SignatureChanged = newModifiers is not null && !oldModifiers.SequenceEqual(newModifiers);
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    // The part's header rewritten: modifiers, keyword, name, type parameters, base list and constraints. Attributes and the body are kept.
    private static TextChange HeaderChange(TypeDeclarationSyntax declaration, IEnumerable<string> modifiers, IReadOnlyList<string>? bases)
    {
        var start = declaration.Modifiers.Count > 0 ? declaration.Modifiers[0].SpanStart : declaration.Keyword.SpanStart;
        var end = declaration.OpenBraceToken.SpanStart;
        var baseText = bases is not null
            ? (bases.Count > 0 ? " : " + string.Join(", ", bases) : "")
            : declaration.BaseList is { } list ? " " + list.ToString() : "";
        var constraints = string.Concat(declaration.ConstraintClauses.Select(c => " " + c.ToString()));
        var header = string.Join(" ", modifiers.Append(declaration.Keyword.ValueText))
            + " " + declaration.Identifier.ValueText
            + (declaration.TypeParameterList?.ToString() ?? "")
            + baseText + constraints + " ";
        return new TextChange(TextSpan.FromBounds(start, end), header);
    }
}
