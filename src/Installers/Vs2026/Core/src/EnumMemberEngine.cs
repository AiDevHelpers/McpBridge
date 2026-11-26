using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Adds a member to an enum, and changes the value of one (<c>ide_add_enum_member</c>, <c>ide_edit_enum_member</c>). A value change is
/// refused for a [Flags] enum, whose values are bits, and for an enum that looks persisted (it has attributes, or is public), whose
/// stored numbers other code may depend on. Both refusals are lifted by allowValueChange. Pure: the caller applies the result.
/// </summary>
internal static class EnumMemberEngine
{
    public static async Task<MethodEditPlan> AddAsync(Solution solution, string file, int line, string name, string? value)
    {
        var plan = new MethodEditPlan();
        var located = await LocateEnumAsync(solution, file, line, plan).ConfigureAwait(false);
        if (located is null)
        {
            return plan;
        }

        var (documentId, enumNode, text) = located.Value;
        if (!SyntaxFacts.IsValidIdentifier(name))
        {
            plan.Reasons.Add(Strings.Format("MemberBadName", name));
            return plan;
        }

        if (enumNode.Members.Any(m => m.Identifier.ValueText == name))
        {
            plan.Reasons.Add(Strings.Format("MemberExists", name));
            return plan;
        }

        if (value is not null && SyntaxFactory.ParseExpression(value).GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            plan.Reasons.Add(Strings.Format("EnumBadValue", value));
            return plan;
        }

        var eol = text.ToString().Contains("\r\n") ? "\r\n" : "\n";
        var member = value is null ? name : name + " = " + value.Trim();
        var lastLine = enumNode.Members.Count > 0 ? enumNode.Members[enumNode.Members.Count - 1] : null;
        var indent = lastLine is not null
            ? MethodAddEngine.LeadingWhitespace(text.Lines.GetLineFromPosition(lastLine.SpanStart).ToString())
            : MethodAddEngine.LeadingWhitespace(text.Lines.GetLineFromPosition(enumNode.OpenBraceToken.SpanStart).ToString()) + "    ";

        TextChange change;
        if (lastLine is null)
        {
            change = new TextChange(new TextSpan(enumNode.OpenBraceToken.Span.End, 0), eol + indent + member);
        }
        else if (enumNode.Members.SeparatorCount == enumNode.Members.Count)
        {
            // A trailing comma already ends the list: the new member goes after it and takes the comma.
            var comma = enumNode.Members.GetSeparator(enumNode.Members.Count - 1);
            change = new TextChange(new TextSpan(comma.Span.End, 0), eol + indent + member + ",");
        }
        else
        {
            change = new TextChange(new TextSpan(lastLine.Span.End, 0), "," + eol + indent + member);
        }

        return await FinishAsync(solution, documentId, change, plan).ConfigureAwait(false);
    }

    public static async Task<MethodEditPlan> EditValueAsync(Solution solution, string file, int line, string value, bool allowValueChange)
    {
        var plan = new MethodEditPlan();
        var located = await LocateEnumAsync(solution, file, line, plan).ConfigureAwait(false);
        if (located is null)
        {
            return plan;
        }

        var (documentId, enumNode, text) = located.Value;
        var position = LinePositionOf(text, line);
        var member = enumNode.Members.FirstOrDefault(m => m.Span.Contains(position));
        if (member is null)
        {
            plan.Reasons.Add(Strings.Get("EnumNotAMember"));
            return plan;
        }

        if (value.Trim().Length == 0 || SyntaxFactory.ParseExpression(value).GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            plan.Reasons.Add(Strings.Format("EnumBadValue", value));
            return plan;
        }

        if (!allowValueChange)
        {
            if (enumNode.AttributeLists.SelectMany(a => a.Attributes).Any(a => a.Name.ToString() is "Flags" or "FlagsAttribute" or "System.Flags" or "System.FlagsAttribute"))
            {
                plan.Reasons.Add(Strings.Get("EnumValueFlags"));
                return plan;
            }

            if (enumNode.AttributeLists.Count > 0 || enumNode.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            {
                plan.Reasons.Add(Strings.Get("EnumValuePersisted"));
                return plan;
            }
        }

        var change = member.EqualsValue is { } equals
            ? new TextChange(equals.Value.Span, value.Trim())
            : new TextChange(new TextSpan(member.Identifier.Span.End, 0), " = " + value.Trim());
        return await FinishAsync(solution, documentId, change, plan).ConfigureAwait(false);
    }

    private static async Task<MethodEditPlan> FinishAsync(Solution solution, DocumentId documentId, TextChange change, MethodEditPlan plan)
    {
        var changes = new System.Collections.Generic.Dictionary<DocumentId, System.Collections.Generic.List<TextChange>>
        {
            [documentId] = new System.Collections.Generic.List<TextChange> { change },
        };
        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        edited = await EditChecks.FormatFileAsync(edited, documentId, plan).ConfigureAwait(false);
        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, new[] { documentId }, plan).ConfigureAwait(false);
        plan.Files.Add(solution.GetDocument(documentId)!.FilePath ?? documentId.ToString());
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    private static async Task<(DocumentId DocumentId, EnumDeclarationSyntax Enum, SourceText Text)?> LocateEnumAsync(
        Solution solution, string file, int line, MethodEditPlan plan)
    {
        var documentId = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault();
        var document = documentId is null ? null : solution.GetDocument(documentId);
        if (document is null)
        {
            plan.Reasons.Add(Strings.Format("NoRoslynDocumentFor", file));
            return null;
        }

        var text = await document.GetTextAsync().ConfigureAwait(false);
        if (line < 1 || line > text.Lines.Count)
        {
            plan.Reasons.Add(Strings.Format("LineOutOfRange", line, file));
            return null;
        }

        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var position = LinePositionOf(text, line);
        var node = root!.FindToken(Math.Min(position, root.FullSpan.End)).Parent?.AncestorsAndSelf()
            .FirstOrDefault(n => n is EnumDeclarationSyntax or EnumMemberDeclarationSyntax);
        var enumNode = node is EnumMemberDeclarationSyntax member ? member.Parent as EnumDeclarationSyntax : node as EnumDeclarationSyntax;
        if (enumNode is null)
        {
            plan.Reasons.Add(Strings.Get("EnumNotAnEnum"));
            return null;
        }

        return (documentId!, enumNode, text);
    }

    private static int LinePositionOf(SourceText text, int line)
    {
        var lineText = text.Lines[line - 1].ToString();
        return text.Lines[line - 1].Start + (lineText.Length - lineText.TrimStart().Length);
    }
}
