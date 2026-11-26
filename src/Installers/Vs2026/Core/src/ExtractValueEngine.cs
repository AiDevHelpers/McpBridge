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
/// Extracts one expression into a named local (<c>var</c>) or constant (<c>const</c>) declared just before the statement that uses it
/// (<c>ide_extract</c>, kinds variable and constant; unit 6.7). The expression is named by its text on the line, which must occur once.
/// A constant needs an expression the compiler folds to a constant value. Pure: the caller applies <see cref="MethodEditPlan.Result"/>.
/// </summary>
internal static class ExtractValueEngine
{
    public static async Task<MethodEditPlan> PlanAsync(Solution solution, string file, int line, string expression, string name, string kind)
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
        if (line < 1 || line > text.Lines.Count || !SyntaxFacts.IsValidIdentifier(name) || expression.Trim().Length == 0 || kind is not ("variable" or "constant"))
        {
            plan.Reasons.Add(Strings.Get("ExtractValueBadInput"));
            return plan;
        }

        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        var lineSpan = text.Lines[line - 1].Span;
        var wanted = expression.Trim();
        var matches = root!.DescendantNodes().OfType<ExpressionSyntax>()
            .Where(e => lineSpan.Contains(e.Span) && e.ToString() == wanted).ToList();
        if (matches.Count != 1)
        {
            plan.Reasons.Add(Strings.Format("ExtractValueNotOnce", wanted, matches.Count));
            return plan;
        }

        var target = matches[0];
        var statement = target.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
        if (statement is null || statement.Parent is not BlockSyntax block)
        {
            plan.Reasons.Add(Strings.Get("ExtractValueNoStatement"));
            return plan;
        }

        if (target.DescendantNodesAndSelf().Any(n => n is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax))
        {
            plan.Reasons.Add(Strings.Get("ExtractValueLambda"));
            return plan;
        }

        string declaration;
        if (kind == "constant")
        {
            var constant = model!.GetConstantValue(target);
            if (!constant.HasValue)
            {
                plan.Reasons.Add(Strings.Get("ExtractValueNotConstant"));
                return plan;
            }

            var type = model.GetTypeInfo(target).Type;
            if (type is null)
            {
                plan.Reasons.Add(Strings.Get("ExtractValueNotConstant"));
                return plan;
            }

            declaration = "const " + type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + " " + name + " = " + wanted + ";";
        }
        else
        {
            declaration = "var " + name + " = " + wanted + ";";
        }

        var eol = text.ToString().Contains("\r\n") ? "\r\n" : "\n";
        var indent = MethodAddEngine.LeadingWhitespace(text.Lines.GetLineFromPosition(statement.SpanStart).ToString());
        var changes = new Dictionary<DocumentId, List<TextChange>>
        {
            [documentId!] = new List<TextChange>
            {
                new TextChange(new TextSpan(text.Lines.GetLineFromPosition(statement.SpanStart).Start, 0), indent + declaration + eol),
                new TextChange(target.Span, name),
            },
        };
        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        edited = await EditChecks.FormatFileAsync(edited, documentId!, plan).ConfigureAwait(false);
        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, new[] { documentId! }, plan).ConfigureAwait(false);
        plan.Files.Add(document.FilePath ?? file);
        plan.Warnings.Add(Strings.Format("ExtractValueDone", kind, name));
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }
}
