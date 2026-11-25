using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>A test method found in the solution: where it lives and the name test runners filter on.</summary>
internal sealed class TestTarget
{
    public string Project { get; set; } = "";

    public string Name { get; set; } = "";

    public string File { get; set; } = "";

    public int Line { get; set; }

    public object ToReply() => new { project = Project, name = Name, file = File, line = Line };
}

/// <summary>Finds the tests that call a method, and where a method sits in its file after an edit. Read-only.</summary>
internal static class TestDiscovery
{
    // xUnit, NUnit and MSTest test attributes, by simple name without the Attribute suffix.
    private static readonly HashSet<string> TestAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "SkippableFact", "SkippableTheory", "Test", "TestCase", "TestCaseSource", "Theory", "TestMethod", "DataTestMethod",
    };

    public static bool IsTestMethod(IMethodSymbol method) =>
        method.GetAttributes().Any(a => a.AttributeClass is { } c && TestAttributes.Contains(Trim(c.Name)));

    private static string Trim(string name) => name.EndsWith("Attribute", StringComparison.Ordinal) ? name.Substring(0, name.Length - 9) : name;

    /// <summary>The name a runner filters on: namespace, then each containing type joined with '+' for nested types, then the method.</summary>
    public static string FullName(IMethodSymbol method) => MetadataName(method.ContainingType) + "." + method.Name;

    public static string MetadataName(INamedTypeSymbol type)
    {
        var name = type.MetadataName;
        if (type.ContainingType is not null)
        {
            return MetadataName(type.ContainingType) + "+" + name;
        }

        return type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + name : name;
    }

    public static TestTarget TargetFor(IMethodSymbol method, Document document, int line) => new()
    {
        Project = document.Project.FilePath ?? document.Project.Name,
        Name = FullName(method),
        File = document.FilePath ?? "",
        Line = line,
    };

    /// <summary>The test methods that contain one of the given references.</summary>
    public static async Task<List<TestTarget>> FindDirectAsync(Solution solution, IEnumerable<MethodEditEngine.Reference> references)
    {
        var found = new Dictionary<string, TestTarget>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            var document = solution.GetDocument(reference.DocumentId);
            if (document is null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
            var enclosing = root?.FindToken(reference.Span.Start).Parent?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            var symbol = enclosing is null || model is null ? null : model.GetDeclaredSymbol(enclosing);
            if (symbol is null || !IsTestMethod(symbol))
            {
                continue;
            }

            var target = TargetFor(symbol, document, enclosing!.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
            found[target.Project + "|" + target.Name] = target;
        }

        return found.Values.ToList();
    }

    /// <summary>Where the method that contains a line sits, for coverage. Null when no method is there.</summary>
    public static async Task<object?> LocateAtAsync(Solution solution, string file, int line)
    {
        var id = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault();
        var document = id is null ? null : solution.GetDocument(id);
        var text = document is null ? null : await document.GetTextAsync().ConfigureAwait(false);
        if (document is null || text is null || line < 1 || line > text.Lines.Count)
        {
            return null;
        }

        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        var lineText = text.Lines[line - 1].ToString();
        var position = text.Lines[line - 1].Start + (lineText.Length - lineText.TrimStart().Length);
        var node = root?.FindToken(Math.Min(position, root.FullSpan.End)).Parent?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        var symbol = node is null || model is null ? null : model.GetDeclaredSymbol(node);
        return symbol is null ? null : await LocateAsync(solution, id!, MetadataName(symbol.ContainingType), MethodEditEngine.SignatureKey(symbol)).ConfigureAwait(false);
    }

    /// <summary>Where a method sits in the edited document, found by its type and signature key. Null when it is not there.</summary>
    public static async Task<object?> LocateAsync(Solution edited, DocumentId id, string typeMetadataName, string signatureKey)
    {
        var document = edited.GetDocument(id);
        var root = document is null ? null : await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = document is null ? null : await document.GetSemanticModelAsync().ConfigureAwait(false);
        if (document is null || root is null || model is null)
        {
            return null;
        }

        foreach (var node in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var symbol = model.GetDeclaredSymbol(node);
            if (symbol is null || MetadataName(symbol.ContainingType) != typeMetadataName || MethodEditEngine.SignatureKey(symbol) != signatureKey)
            {
                continue;
            }

            var start = node.Modifiers.Count > 0 ? node.Modifiers[0].SpanStart : node.ReturnType.SpanStart;
            var lines = node.SyntaxTree.GetLineSpan(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(start, node.Span.End));
            return new
            {
                file = document.FilePath,
                name = symbol.Name,
                containing_type = typeMetadataName,
                start_line = lines.StartLinePosition.Line + 1,
                end_line = lines.EndLinePosition.Line + 1,
            };
        }

        return null;
    }
}
