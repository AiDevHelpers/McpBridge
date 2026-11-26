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
/// Adds a new type in its own file (<c>ide_add_type</c>). The file goes beside the anchor file, in the anchor's project and namespace
/// unless a namespace is given. The caller gives the declaration; the engine writes the usings, the summary and attributes around it in
/// the file's own layout, and checks the result like any other edit. Pure: the caller applies <see cref="MethodEditPlan.Result"/>.
/// </summary>
internal static class AddTypeEngine
{
    public static async Task<MethodEditPlan> AddAsync(
        Solution solution, string anchorFile, string declaration, IReadOnlyList<string> usings, string? namespaceName, string? summary, IReadOnlyList<string> attributes)
    {
        var plan = new MethodEditPlan();
        var anchorId = solution.GetDocumentIdsWithFilePath(anchorFile).FirstOrDefault();
        var anchor = anchorId is null ? null : solution.GetDocument(anchorId);
        if (anchor is null)
        {
            plan.Reasons.Add(Strings.Format("NoRoslynDocumentFor", anchorFile));
            return plan;
        }

        var parsed = SyntaxFactory.ParseMemberDeclaration(declaration.Trim());
        if (parsed is not BaseTypeDeclarationSyntax and not DelegateDeclarationSyntax || parsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            plan.Reasons.Add(Strings.Format("AddTypeBadDeclaration", declaration.Trim()));
            return plan;
        }

        var name = parsed switch
        {
            BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
            DelegateDeclarationSyntax del => del.Identifier.ValueText,
            _ => "",
        };
        var compilation = await anchor.Project.GetCompilationAsync().ConfigureAwait(false);
        if (compilation is not null && compilation.GetSymbolsWithName(name, SymbolFilter.Type).Any())
        {
            plan.Reasons.Add(Strings.Format("AddTypeExists", name));
            return plan;
        }

        var directory = Path.GetDirectoryName(anchor.FilePath ?? anchorFile) ?? "";
        var path = Path.Combine(directory, name + ".cs");
        if (solution.GetDocumentIdsWithFilePath(path).Length > 0)
        {
            plan.Reasons.Add(Strings.Format("AddTypeFileExists", path));
            return plan;
        }

        var root = await anchor.GetSyntaxRootAsync().ConfigureAwait(false);
        var anchorNamespace = root is null ? "" : root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "";
        var ns = namespaceName is { } given && given.Trim().Length > 0 ? given.Trim() : anchorNamespace;

        var eol = (await anchor.GetTextAsync().ConfigureAwait(false)).ToString().Contains("\r\n") ? "\r\n" : "\n";
        var body = new List<string>();
        if (summary is { } s && s.Trim().Length > 0)
        {
            body.Add("/// <summary>" + s.Trim() + "</summary>");
        }

        body.AddRange(attributes.Select(MemberRenderer.NormalizeAttribute));
        body.AddRange(declaration.Trim().Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()));

        var lines = new List<string>();
        lines.AddRange(usings.Where(u => u.Trim().Length > 0).Select(u => "using " + u.Trim().TrimEnd(';') + ";"));
        if (lines.Count > 0)
        {
            lines.Add("");
        }

        if (ns.Length > 0)
        {
            lines.Add("namespace " + ns);
            lines.Add("{");
            lines.AddRange(body.Select(l => l.Length == 0 ? "" : "    " + l));
            lines.Add("}");
        }
        else
        {
            lines.AddRange(body);
        }

        var text = string.Join(eol, lines) + eol;
        var edited = solution.AddDocument(DocumentId.CreateNewId(anchor.Project.Id), Path.GetFileName(path), SourceText.From(text, System.Text.Encoding.UTF8), filePath: path);
        var newId = edited.GetDocumentIdsWithFilePath(path).Single();
        edited = await EditChecks.FormatFileAsync(edited, newId, plan).ConfigureAwait(false);
        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, new[] { newId }, plan).ConfigureAwait(false);
        plan.Files.Add(path);
        plan.Warnings.Add(Strings.Get("AddTypeFileNew"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }
}
