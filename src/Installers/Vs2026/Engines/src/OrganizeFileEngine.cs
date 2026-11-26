using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// File layout changes (<c>ide_organize_file</c>, unit 5.4). "split" moves a top-level type out of a file that holds several types into
/// its own file named after it, beside the original, with the original's usings and namespace. "rename" renames a file that holds one
/// type to that type's name. Pure: the caller applies <see cref="MethodEditPlan.Result"/> and any <see cref="MethodEditPlan.FileMoves"/>.
/// </summary>
internal static class OrganizeFileEngine
{
    public static async Task<MethodEditPlan> PlanAsync(Solution solution, string file, int line, string action)
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
        var types = root!.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(t => t.Parent is NamespaceDeclarationSyntax or CompilationUnitSyntax or FileScopedNamespaceDeclarationSyntax).ToList();
        if (types.Count == 0)
        {
            plan.Reasons.Add(Strings.Get("OrganizeNoTypes"));
            return plan;
        }

        switch (action)
        {
            case "split":
                return await SplitAsync(solution, document, documentId!, root, text, types, line, plan).ConfigureAwait(false);
            case "rename":
                return RenameFile(solution, document, types, plan);
            default:
                plan.Reasons.Add(Strings.Format("OrganizeBadAction", action));
                return plan;
        }
    }

    private static async Task<MethodEditPlan> SplitAsync(
        Solution solution, Document document, DocumentId documentId, SyntaxNode root, SourceText text, List<TypeDeclarationSyntax> types, int line, MethodEditPlan plan)
    {
        var lineText = text.Lines[line - 1].ToString();
        var position = text.Lines[line - 1].Start + (lineText.Length - lineText.TrimStart().Length);
        var type = types.FirstOrDefault(t => t.FullSpan.Contains(position));
        if (type is null)
        {
            plan.Reasons.Add(Strings.Get("OrganizeNotATopLevelType"));
            return plan;
        }

        if (types.Count < 2)
        {
            plan.Reasons.Add(Strings.Get("OrganizeNothingToSplit"));
            return plan;
        }

        var directory = Path.GetDirectoryName(document.FilePath ?? "") ?? "";
        var path = Path.Combine(directory, type.Identifier.ValueText + ".cs");
        if (solution.GetDocumentIdsWithFilePath(path).Length > 0)
        {
            plan.Reasons.Add(Strings.Format("AddTypeFileExists", path));
            return plan;
        }

        var eol = text.ToString().Contains("\r\n") ? "\r\n" : "\n";
        var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => u.WithoutTrivia().ToString()).Distinct().ToList();
        var ns = type.Parent is BaseNamespaceDeclarationSyntax nsDecl ? nsDecl.Name.ToString() : "";
        var declaration = type.ToFullString().Trim().Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        var lines = new List<string>();
        lines.AddRange(usings);
        if (usings.Count > 0)
        {
            lines.Add("");
        }

        if (ns.Length > 0)
        {
            lines.Add("namespace " + ns);
            lines.Add("{");
            lines.AddRange(declaration.Select(l => l.Length == 0 ? "" : "    " + l));
            lines.Add("}");
        }
        else
        {
            lines.AddRange(declaration);
        }

        var newText = string.Join(eol, lines) + eol;
        var changes = new Dictionary<DocumentId, List<TextChange>>
        {
            [documentId] = new List<TextChange> { new TextChange(type.FullSpan, "") },
        };
        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        edited = edited.AddDocument(DocumentId.CreateNewId(document.Project.Id), Path.GetFileName(path), SourceText.From(newText, System.Text.Encoding.UTF8), filePath: path);
        var newId = edited.GetDocumentIdsWithFilePath(path).Single();
        edited = await EditChecks.FormatFileAsync(edited, documentId, plan).ConfigureAwait(false);
        edited = await EditChecks.FormatFileAsync(edited, newId, plan).ConfigureAwait(false);
        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, new[] { documentId, newId }, plan).ConfigureAwait(false);
        plan.Files.Add(document.FilePath ?? "");
        plan.Files.Add(path);
        plan.Warnings.Add(Strings.Format("OrganizeSplitDone", type.Identifier.ValueText, path));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    private static MethodEditPlan RenameFile(Solution solution, Document document, List<TypeDeclarationSyntax> types, MethodEditPlan plan)
    {
        if (types.Count != 1)
        {
            plan.Reasons.Add(Strings.Get("OrganizeRenameOneType"));
            return plan;
        }

        var name = types[0].Identifier.ValueText;
        var from = document.FilePath ?? "";
        if (Path.GetFileNameWithoutExtension(from) == name)
        {
            plan.Reasons.Add(Strings.Get("OrganizeAlreadyNamed"));
            return plan;
        }

        var to = Path.Combine(Path.GetDirectoryName(from) ?? "", name + ".cs");
        if (solution.GetDocumentIdsWithFilePath(to).Length > 0)
        {
            plan.Reasons.Add(Strings.Format("AddTypeFileExists", to));
            return plan;
        }

        plan.FileMoves.Add(new FileMove(from, to, document.Project.Name));
        plan.Result = solution;
        plan.Files.Add(to);
        plan.Warnings.Add(Strings.Format("OrganizeRenamed", from, to));
        return plan;
    }
}
