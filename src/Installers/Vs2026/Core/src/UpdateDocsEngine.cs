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
/// Edits the doc comment of the method at a line, with no code change (<c>ide_update_docs</c>, unit 8.3). The summary and the param, typeparam
/// and returns texts are given by the caller; the layout is the comment's own (<see cref="MethodDocUpdater"/>). Text is never invented: a tag
/// the caller does not give is left as it is. Pure: the caller applies <see cref="MethodEditPlan.Result"/>.
/// </summary>
internal static class UpdateDocsEngine
{
    public static async Task<MethodEditPlan> PlanAsync(Solution solution, string file, int line, string? summary, IReadOnlyList<DocItem> items)
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
        if (line < 1 || line > text.Lines.Count || (summary is null && items.Count == 0))
        {
            plan.Reasons.Add(Strings.Get("UpdateDocsNothing"));
            return plan;
        }

        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        var lineText = text.Lines[line - 1].ToString();
        var position = text.Lines[line - 1].Start + (lineText.Length - lineText.TrimStart().Length);
        var method = root!.FindToken(Math.Min(position, root.FullSpan.End)).Parent?.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        if (method is null || model!.GetDeclaredSymbol(method) is not IMethodSymbol symbol)
        {
            plan.Reasons.Add(Strings.Get("MethodEditNotAMethod"));
            return plan;
        }

        var eol = text.ToString().Contains("\r\n") ? "\r\n" : "\n";
        var updates = new DocUpdates { NewSummary = summary };
        updates.Items.AddRange(items);
        var result = MethodDocUpdater.Update(method, symbol, symbol, updates, false, eol);
        plan.Warnings.AddRange(result.Warnings);
        if (result.Missing.Count > 0)
        {
            plan.Reasons.Add(Strings.Format("MethodEditDocsMissing", string.Join(", ", result.Missing)));
            return plan;
        }

        if (result.Change is not { } change)
        {
            plan.Reasons.Add(Strings.Get("UpdateDocsNoComment"));
            return plan;
        }

        var changes = new Dictionary<DocumentId, List<TextChange>> { [documentId!] = new List<TextChange> { change } };
        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, new[] { documentId! }, plan).ConfigureAwait(false);
        plan.Files.Add(document.FilePath ?? file);
        plan.DocsUpdated = true;
        plan.Warnings.Add(Strings.Get("UpdateDocsDone"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }
}
