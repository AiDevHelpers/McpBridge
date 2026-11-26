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
/// Adds a constructor to a class, struct or record that takes the chosen fields and properties and assigns each one
/// (<c>ide_generate_members</c>, kind constructor). Parameter names are the member names with the first letter lower-cased and any
/// leading underscore dropped. A constructor with the same parameter types as an existing one is refused. Pure: the caller applies the result.
/// </summary>
internal static class GenerateConstructorEngine
{
    public static async Task<MethodEditPlan> PlanAsync(Solution solution, string file, int line, IReadOnlyList<string> memberNames, string accessibility)
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

        if (memberNames.Count == 0)
        {
            plan.Reasons.Add(Strings.Get("GenerateCtorNoMembers"));
            return plan;
        }

        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        var lineText = text.Lines[line - 1].ToString();
        var position = text.Lines[line - 1].Start + (lineText.Length - lineText.TrimStart().Length);
        var type = root!.FindToken(Math.Min(position, root.FullSpan.End)).Parent?.AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>().FirstOrDefault(t => t is not InterfaceDeclarationSyntax);
        if (type is null || model!.GetDeclaredSymbol(type) is not INamedTypeSymbol symbol)
        {
            plan.Reasons.Add(Strings.Get("ImplementNotAType"));
            return plan;
        }

        if (accessibility is not ("public" or "internal" or "protected" or "private"))
        {
            plan.Reasons.Add(Strings.Format("ChangeSignatureBadHeader", accessibility));
            return plan;
        }

        var chosen = new List<ISymbol>();
        foreach (var name in memberNames)
        {
            var member = symbol.GetMembers(name).FirstOrDefault(m => !m.IsStatic && m is IFieldSymbol or IPropertySymbol);
            if (member is IPropertySymbol { SetMethod: null })
            {
                plan.Reasons.Add(Strings.Format("GenerateCtorNoSetter", name));
                return plan;
            }

            if (member is null)
            {
                plan.Reasons.Add(Strings.Format("GenerateCtorNoMember", name));
                return plan;
            }

            chosen.Add(member);
        }

        var parameters = chosen.Select(m => (Name: ParameterName(m.Name), Type: MemberType(m))).ToList();
        var signature = string.Join(", ", parameters.Select(p => p.Type + " " + p.Name));
        var existing = symbol.Constructors.Where(c => !c.IsStatic && !c.IsImplicitlyDeclared).ToList();
        var key = string.Join(",", parameters.Select(p => p.Type));
        if (existing.Any(c => string.Join(",", c.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))) == key))
        {
            plan.Reasons.Add(Strings.Format("GenerateCtorExists", symbol.Name, signature));
            return plan;
        }

        var eol = text.ToString().Contains("\r\n") ? "\r\n" : "\n";
        var indent = type.Members.Count > 0
            ? MethodAddEngine.LeadingWhitespace(text.Lines.GetLineFromPosition(type.Members[0].SpanStart).ToString())
            : MethodAddEngine.LeadingWhitespace(text.Lines.GetLineFromPosition(type.CloseBraceToken.SpanStart).ToString()) + "    ";
        var lines = new List<string> { accessibility + " " + symbol.Name + "(" + signature + ")", "{" };
        for (var i = 0; i < chosen.Count; i++)
        {
            lines.Add("    this." + chosen[i].Name + " = " + parameters[i].Name + ";");
        }

        lines.Add("}");
        var block = string.Join(eol, lines.Select(l => indent + l));
        var last = type.Members.LastOrDefault();
        var change = last is null
            ? new TextChange(new TextSpan(type.OpenBraceToken.Span.End, 0), eol + block)
            : new TextChange(new TextSpan(last.Span.End, 0), eol + eol + block);

        var changes = new Dictionary<DocumentId, List<TextChange>> { [documentId!] = new List<TextChange> { change } };
        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        edited = await EditChecks.FormatFileAsync(edited, documentId!, plan).ConfigureAwait(false);
        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, new[] { documentId! }, plan).ConfigureAwait(false);
        plan.Files.Add(document.FilePath ?? file);
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    private static string MemberType(ISymbol member) => member switch
    {
        IFieldSymbol field => field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        IPropertySymbol property => property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        _ => "object",
    };

    // The parameter name for a member: no leading underscore, first letter lower-cased, and "@" when it would be a keyword.
    internal static string ParameterName(string member)
    {
        var trimmed = member.TrimStart('_');
        if (trimmed.Length == 0)
        {
            trimmed = member;
        }

        var name = char.ToLowerInvariant(trimmed[0]) + trimmed.Substring(1);
        return SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
    }
}
