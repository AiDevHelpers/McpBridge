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
/// Adds <c>Equals(object)</c>, <c>GetHashCode()</c> or <c>ToString()</c> to a class, struct or record, over the chosen fields and
/// properties (<c>ide_generate_members</c>, kinds equals, hashcode and tostring). The body is written in the form the type is most
/// likely to use: a plain comparison and a <c>HashCode.Combine</c> call. Refused when the type already declares that member. Pure: the caller
/// applies the result.
/// </summary>
internal static class GenerateMembersEngine
{
    public static async Task<MethodEditPlan> PlanAsync(Solution solution, string file, int line, string kind, IReadOnlyList<string> memberNames)
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

        if (kind is not ("equals" or "hashcode" or "tostring"))
        {
            plan.Reasons.Add(Strings.Format("GenerateMembersBadKind", kind));
            return plan;
        }

        if (memberNames.Count == 0)
        {
            plan.Reasons.Add(Strings.Get("GenerateMembersNoMembers"));
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

        var wanted = kind switch
        {
            "equals" => "Equals",
            "hashcode" => "GetHashCode",
            _ => "ToString",
        };
        if (symbol.GetMembers(wanted).Any(m => m is IMethodSymbol { IsImplicitlyDeclared: false, Parameters.Length: 0 } or IMethodSymbol { Parameters.Length: 1 }))
        {
            plan.Reasons.Add(Strings.Format("GenerateMembersExists", wanted));
            return plan;
        }

        var members = new List<ISymbol>();
        foreach (var name in memberNames)
        {
            var member = symbol.GetMembers(name).FirstOrDefault(m => !m.IsStatic && m is IFieldSymbol or IPropertySymbol);
            if (member is null)
            {
                plan.Reasons.Add(Strings.Format("GenerateCtorNoMember", name));
                return plan;
            }

            members.Add(member);
        }

        var lines = new List<string>();
        switch (kind)
        {
            case "equals":
                lines.Add("public override bool Equals(object? obj)");
                lines.Add("{");
                lines.Add("    return obj is " + symbol.Name + " other");
                lines.AddRange(members.Select(m => "        && System.Collections.Generic.EqualityComparer<" + TypeText(m) + ">.Default.Equals(" + m.Name + ", other." + m.Name + ")"));
                lines[lines.Count - 1] += ";";
                lines.Add("}");
                break;
            case "hashcode":
                lines.Add("public override int GetHashCode()");
                lines.Add("{");
                // A plain combination works on every target framework, unlike System.HashCode, which .NET Framework lacks.
                lines.Add("    unchecked");
                lines.Add("    {");
                lines.Add("        int hash = 17;");
                lines.AddRange(members.Select(m => "        hash = hash * 23 + System.Collections.Generic.EqualityComparer<" + TypeText(m) + ">.Default.GetHashCode(" + m.Name + ");"));
                lines.Add("        return hash;");
                lines.Add("    }");
                lines.Add("}");
                break;
            default:
                lines.Add("public override string ToString()");
                lines.Add("{");
                lines.Add("    return $\"" + symbol.Name + " {{ " + string.Join(", ", members.Select(m => m.Name + " = {" + m.Name + "}")) + " }}\";");
                lines.Add("}");
                break;
        }

        var eol = text.ToString().Contains("\r\n") ? "\r\n" : "\n";
        var indent = type.Members.Count > 0
            ? MethodAddEngine.LeadingWhitespace(text.Lines.GetLineFromPosition(type.Members[0].SpanStart).ToString())
            : MethodAddEngine.LeadingWhitespace(text.Lines.GetLineFromPosition(type.CloseBraceToken.SpanStart).ToString()) + "    ";
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

    private static string TypeText(ISymbol member) => member switch
    {
        IFieldSymbol field => field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        IPropertySymbol property => property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        _ => "object",
    };
}
