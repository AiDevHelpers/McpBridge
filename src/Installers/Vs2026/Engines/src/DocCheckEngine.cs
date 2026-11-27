using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Read-only check of the XML doc comments of the methods in a file (<c>ide_check_docs</c>, unit 8.2). Reports a parameter or type
/// parameter with no tag, a tag that names nothing, and a non-void method with no returns tag. Nothing is changed.
/// </summary>
internal static class DocCheckEngine
{
    public static async Task<List<object>> CheckAsync(Solution solution, string file)
    {
        var found = new List<object>();
        var documentId = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault();
        var document = documentId is null ? null : solution.GetDocument(documentId);
        var root = document is null ? null : await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = document is null ? null : await document.GetSemanticModelAsync().ConfigureAwait(false);
        if (root is null || model is null)
        {
            return found;
        }

        foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
        {
            var doc = method.GetLeadingTrivia().Select(t => t.GetStructure()).OfType<DocumentationCommentTriviaSyntax>().FirstOrDefault();
            if (doc is null || model.GetDeclaredSymbol(method) is not IMethodSymbol symbol)
            {
                continue;
            }

            var tags = doc.Content.OfType<XmlElementSyntax>().ToList();
            var documentedParams = Names(tags, "param");
            var documentedTypeParams = Names(tags, "typeparam");
            var actualParams = symbol.Parameters.Select(p => p.Name).ToList();
            var actualTypeParams = symbol.TypeParameters.Select(p => p.Name).ToList();
            var report = new
            {
                file,
                method = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                line = doc.SpanStart,
                missing_param = actualParams.Except(documentedParams).ToList(),
                extra_param = documentedParams.Except(actualParams).ToList(),
                missing_typeparam = actualTypeParams.Except(documentedTypeParams).ToList(),
                extra_typeparam = documentedTypeParams.Except(actualTypeParams).ToList(),
                missing_returns = !symbol.ReturnsVoid && !tags.Any(t => t.StartTag.Name.LocalName.ValueText == "returns"),
            };
            if (report.missing_param.Count > 0 || report.extra_param.Count > 0 || report.missing_typeparam.Count > 0
                || report.extra_typeparam.Count > 0 || report.missing_returns)
            {
                found.Add(report);
            }
        }

        return found;
    }

    private static List<string> Names(IEnumerable<XmlElementSyntax> tags, string tag) =>
        tags.Where(t => t.StartTag.Name.LocalName.ValueText == tag)
            .Select(t => t.StartTag.Attributes.OfType<XmlNameAttributeSyntax>().FirstOrDefault()?.Identifier.Identifier.ValueText ?? "")
            .Where(n => n.Length > 0)
            .ToList();
}
