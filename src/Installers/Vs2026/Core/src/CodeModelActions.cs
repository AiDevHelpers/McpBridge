using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Code elements of one file from Roslyn, through the Visual Studio workspace. Replaces the EnvDTE code model, which the
/// IDE's C# implementation refuses (E_FAIL). Must run on the UI thread; the async Roslyn calls run through the join factory.
/// </summary>
internal static class CodeModelActions
{
    public static object ListCodeElements(string file)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ThreadHelper.JoinableTaskFactory.Run(() => ListCodeElementsAsync(file));
    }

    private static async Task<object> ListCodeElementsAsync(string file)
    {
        var model = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel
            ?? throw new InvalidOperationException(Strings.Get("ComponentModelUnavailable"));
        var workspace = model.GetService<VisualStudioWorkspace>();
        var solution = workspace.CurrentSolution;
        var documentId = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault()
            ?? throw new ArgumentException(Strings.Format("NoRoslynDocumentFor", file));
        var document = solution.GetDocument(documentId)
            ?? throw new ArgumentException(Strings.Format("NoRoslynDocumentFor", file));

        var tree = await document.GetSyntaxTreeAsync().ConfigureAwait(true)
            ?? throw new InvalidOperationException(Strings.Format("NoSyntaxTreeFor", file));
        var root = await tree.GetRootAsync().ConfigureAwait(true);

        var list = new List<object>();
        foreach (var node in root.DescendantNodes())
        {
            var name = NameOf(node);
            if (name is null)
            {
                continue;
            }

            var span = tree.GetLineSpan(node.Span);
            list.Add(new
            {
                kind = node.Kind().ToString(),
                name,
                start_line = span.StartLinePosition.Line + 1,
                end_line = span.EndLinePosition.Line + 1,
            });
        }

        return new { elements = list };
    }

    // Names only the declarations an agent navigates by: namespaces, types, methods, constructors, properties.
    private static string? NameOf(SyntaxNode node) => node switch
    {
        NamespaceDeclarationSyntax ns => ns.Name.ToString(),
        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        ConstructorDeclarationSyntax ctor => ctor.Identifier.ValueText,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        _ => null,
    };
}
