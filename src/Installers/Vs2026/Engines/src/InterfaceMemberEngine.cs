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
/// Adds a method, property, event or indexer to an interface (<c>ide_add_interface_member</c>). Every type that implements the interface
/// would then lack the member, so the plan lists each implementer and refuses while there are any. Pure: the caller applies the result.
/// </summary>
internal static class InterfaceMemberEngine
{
    public static async Task<MethodEditPlan> AddAsync(Solution solution, string file, int line, string declaration)
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
        var type = root!.FindToken(Math.Min(position, root.FullSpan.End)).Parent?.AncestorsAndSelf().OfType<InterfaceDeclarationSyntax>().FirstOrDefault();
        if (type is null)
        {
            plan.Reasons.Add(Strings.Get("InterfaceNotAnInterface"));
            return plan;
        }

        var parsed = SyntaxFactory.ParseMemberDeclaration(declaration.Trim());
        if (parsed is not (MethodDeclarationSyntax or PropertyDeclarationSyntax or EventDeclarationSyntax or IndexerDeclarationSyntax)
            || parsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            plan.Reasons.Add(Strings.Format("InterfaceBadMember", declaration.Trim()));
            return plan;
        }

        var names = MemberAddEngine.NamesOf(parsed).ToList();
        if (type.Members.SelectMany(MemberAddEngine.NamesOf).Any(n => names.Contains(n)))
        {
            plan.Reasons.Add(Strings.Format("MemberExists", string.Join(", ", names)));
            return plan;
        }

        var symbol = model!.GetDeclaredSymbol(type) as INamedTypeSymbol;
        if (symbol is not null)
        {
            var implementers = (await SymbolFinder.FindImplementationsAsync(symbol, solution).ConfigureAwait(false))
                .OfType<INamedTypeSymbol>()
                .Where(t => t.Locations.Any(l => l.IsInSource))
                .Select(t => t.ToDisplayString())
                .Distinct()
                .ToList();
            if (implementers.Count > 0)
            {
                plan.Reasons.Add(Strings.Format("InterfaceImplementers", string.Join(", ", implementers)));
                return plan;
            }
        }

        var eol = text.ToString().Contains("\r\n") ? "\r\n" : "\n";
        var indent = type.Members.Count > 0
            ? MethodAddEngine.LeadingWhitespace(text.Lines.GetLineFromPosition(type.Members[0].SpanStart).ToString())
            : MethodAddEngine.LeadingWhitespace(text.Lines.GetLineFromPosition(type.CloseBraceToken.SpanStart).ToString()) + "    ";
        var lines = declaration.Trim().Replace("\r\n", "\n").Split('\n').Select(l => l.Length == 0 ? "" : indent + l.TrimEnd());
        var last = type.Members.LastOrDefault();
        var change = last is null
            ? new TextChange(new TextSpan(type.OpenBraceToken.Span.End, 0), eol + string.Join(eol, lines))
            : new TextChange(new TextSpan(last.Span.End, 0), eol + eol + string.Join(eol, lines));

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
}
