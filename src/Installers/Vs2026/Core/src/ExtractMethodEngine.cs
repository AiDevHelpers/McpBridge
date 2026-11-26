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
/// Extracts a run of statements in one block into a new private method (<c>ide_extract</c>, kind method; units 6.4 and 6.5). Data flow
/// decides the parameters (variables read in the range, declared outside it) and the return value (the one variable used after it).
/// The range is refused when it has more than one value used after it, when it contains return, yield, break or continue, when it is
/// not a whole run of statements in one block, or when the name is taken. The statements are replaced by a call; the new method goes
/// after the enclosing method. Pure: the caller applies <see cref="MethodEditPlan.Result"/>.
/// </summary>
internal static class ExtractMethodEngine
{
    public static async Task<MethodEditPlan> PlanAsync(Solution solution, string file, int startLine, int endLine, string name, string accessibility)
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
        if (startLine < 1 || endLine > text.Lines.Count || startLine > endLine || !SyntaxFacts.IsValidIdentifier(name))
        {
            plan.Reasons.Add(Strings.Get("ExtractBadRange"));
            return plan;
        }

        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        var startPos = text.Lines[startLine - 1].Start;
        var endPos = text.Lines[endLine - 1].End;
        var statements = root!.DescendantNodes().OfType<StatementSyntax>()
            .Where(s => s.Parent is BlockSyntax && s.SpanStart >= startPos && s.Span.End <= endPos).ToList();
        if (statements.Count == 0)
        {
            plan.Reasons.Add(Strings.Get("ExtractNoStatements"));
            return plan;
        }

        var block = (BlockSyntax)statements[0].Parent!;
        if (statements.Any(s => !ReferenceEquals(s.Parent, block)) || statements.Count != block.Statements.Count(s => s.SpanStart >= startPos && s.Span.End <= endPos))
        {
            plan.Reasons.Add(Strings.Get("ExtractNotWholeStatements"));
            return plan;
        }

        var first = statements[0];
        var last = statements[statements.Count - 1];
        var jumps = statements.SelectMany(s => s.DescendantNodesAndSelf()).Any(n => n is ReturnStatementSyntax or YieldStatementSyntax or BreakStatementSyntax or ContinueStatementSyntax);
        if (jumps)
        {
            plan.Reasons.Add(Strings.Get("ExtractJumps"));
            return plan;
        }

        var method = block.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        var methodSymbol = method is null || model is null ? null : model.GetDeclaredSymbol(method) as IMethodSymbol;
        if (method is null || methodSymbol is null)
        {
            plan.Reasons.Add(Strings.Get("ExtractNoMethod"));
            return plan;
        }

        if (methodSymbol.ContainingType.GetMembers(name).Any(m => m is IMethodSymbol))
        {
            plan.Reasons.Add(Strings.Format("MemberExists", name));
            return plan;
        }

        var flow = model!.AnalyzeDataFlow(first, last);
        if (!flow.Succeeded)
        {
            plan.Reasons.Add(Strings.Get("ExtractNoFlow"));
            return plan;
        }

        var outs = flow.DataFlowsOut.Where(s => s is ILocalSymbol or IParameterSymbol).ToList();
        if (outs.Count > 1)
        {
            plan.Reasons.Add(Strings.Format("ExtractManyOutputs", string.Join(", ", outs.Select(o => o.Name))));
            return plan;
        }

        var ins = flow.DataFlowsIn.Where(s => s is ILocalSymbol or IParameterSymbol).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        var parameters = string.Join(", ", ins.Select(s => TypeOf(s) + " " + s.Name));
        var outSymbol = outs.FirstOrDefault();
        var returnType = outSymbol is null ? "void" : TypeOf(outSymbol);
        var isStatic = method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));

        var eol = text.ToString().Contains("\r\n") ? "\r\n" : "\n";
        var callIndent = MethodAddEngine.LeadingWhitespace(lineOf(text, startLine));
        var methodIndent = MethodAddEngine.LeadingWhitespace(lineOf(text, text.Lines.GetLineFromPosition(method.SpanStart).LineNumber + 1));
        var bodyIndent = methodIndent + "    ";

        var body = Enumerable.Range(startLine, endLine - startLine + 1)
            .Select(n => lineOf(text, n))
            .Select(l => l.Length == 0 ? "" : bodyIndent + (l.StartsWith(callIndent, StringComparison.Ordinal) ? l.Substring(callIndent.Length) : l.TrimStart()))
            .ToList();
        var declaredInside = outSymbol is not null && outSymbol.DeclaringSyntaxReferences.Any(r => r.Span.Start >= first.SpanStart && r.Span.End <= last.Span.End);

        var newMethod = new List<string>
        {
            methodIndent + (accessibility.Length > 0 ? accessibility + " " : "private ") + (isStatic ? "static " : "") + returnType + " " + name + "(" + parameters + ")",
            methodIndent + "{",
        };
        newMethod.AddRange(body);
        if (outSymbol is not null)
        {
            newMethod.Add(bodyIndent + "return " + outSymbol.Name + ";");
        }

        newMethod.Add(methodIndent + "}");

        var call = (outSymbol is null ? "" : (declaredInside ? returnType + " " : "") + outSymbol.Name + " = ")
            + name + "(" + string.Join(", ", ins.Select(s => s.Name)) + ");";
        var changes = new Dictionary<DocumentId, List<TextChange>>
        {
            [documentId!] = new List<TextChange>
            {
                new TextChange(new TextSpan(startPos, endPos - startPos), callIndent + call),
                new TextChange(new TextSpan(method.Span.End, 0), eol + eol + string.Join(eol, newMethod)),
            },
        };

        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        edited = await EditChecks.FormatFileAsync(edited, documentId!, plan).ConfigureAwait(false);
        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, new[] { documentId! }, plan).ConfigureAwait(false);
        plan.Files.Add(document.FilePath ?? file);
        plan.Warnings.Add(Strings.Format("ExtractDone", name, parameters.Length == 0 ? "none" : parameters));
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    private static string lineOf(SourceText text, int line) => text.Lines[line - 1].ToString().TrimEnd('\r', '\n');

    private static string TypeOf(ISymbol symbol) => symbol switch
    {
        ILocalSymbol local => local.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        IParameterSymbol parameter => parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        _ => "object",
    };
}
