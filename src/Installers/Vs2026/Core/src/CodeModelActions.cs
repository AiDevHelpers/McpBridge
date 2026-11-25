using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
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

    // Symbol at a position: every reference to it across the solution. Read-only.
    public static object SemanticReferences(string file, int line, int column)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ThreadHelper.JoinableTaskFactory.Run(() => SemanticReferencesAsync(file, line, column));
    }

    // Symbol at a position: where it is declared in source. Read-only.
    public static object SemanticDefinition(string file, int line, int column)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ThreadHelper.JoinableTaskFactory.Run(() => SemanticDefinitionAsync(file, line, column));
    }

    private static async Task<object> SemanticReferencesAsync(string file, int line, int column)
    {
        var (solution, document, position) = await ResolvePositionAsync(file, line, column).ConfigureAwait(true);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position).ConfigureAwait(true)
            ?? throw new ArgumentException(Strings.Get("NoSymbolAtPosition"));

        // One referenced symbol comes back per project, so the same location can repeat. Report each location once.
        var found = new List<object>();
        var seen = new HashSet<(string File, int Line, int Column)>();
        foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbol, solution).ConfigureAwait(true))
        {
            foreach (var reference in referenced.Locations)
            {
                var location = (File: reference.Location.GetLineSpan().Path,
                    Line: reference.Location.GetLineSpan().StartLinePosition.Line,
                    Column: reference.Location.GetLineSpan().StartLinePosition.Character);
                if (seen.Add(location))
                {
                    found.Add(ToLocation(reference.Location));
                }
            }
        }

        return new { symbol = symbol.ToDisplayString(), locations = found };
    }

    private static async Task<object> SemanticDefinitionAsync(string file, int line, int column)
    {
        var (_, document, position) = await ResolvePositionAsync(file, line, column).ConfigureAwait(true);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position).ConfigureAwait(true)
            ?? throw new ArgumentException(Strings.Get("NoSymbolAtPosition"));

        var locations = new List<object>();
        foreach (var location in symbol.Locations)
        {
            if (location.IsInSource)
            {
                locations.Add(ToLocation(location));
            }
        }

        return new { symbol = symbol.ToDisplayString(), locations };
    }

    // Renames the symbol at a position across the solution. Plan-only lists the files that would change. Applied writes them
    // through the VS workspace, so the change is one undoable edit set in the IDE.
    public static object RenameSymbol(string file, int line, int column, string newName, bool confirm)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ThreadHelper.JoinableTaskFactory.Run(() => RenameSymbolAsync(file, line, column, newName, confirm));
    }

    private static async Task<object> RenameSymbolAsync(string file, int line, int column, string newName, bool confirm)
    {
        var (solution, document, position) = await ResolvePositionAsync(file, line, column).ConfigureAwait(true);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position).ConfigureAwait(true)
            ?? throw new ArgumentException(Strings.Get("NoSymbolAtPosition"));

        var renamed = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName).ConfigureAwait(true);
        var files = new List<string>();
        foreach (var projectChanges in renamed.GetChanges(solution).GetProjectChanges())
        {
            foreach (var documentId in projectChanges.GetChangedDocuments())
            {
                var path = renamed.GetDocument(documentId)?.FilePath;
                if (path is not null)
                {
                    files.Add(path);
                }
            }
        }

        var change = new { symbol = symbol.ToDisplayString(), new_name = newName, files };
        if (!confirm)
        {
            return new { planned = true, would = change };
        }

        var model = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel
            ?? throw new InvalidOperationException(Strings.Get("ComponentModelUnavailable"));
        var workspace = model.GetService<VisualStudioWorkspace>();
        if (!workspace.TryApplyChanges(renamed))
        {
            throw new InvalidOperationException(Strings.Get("RenameNotApplied"));
        }

        return new { planned = false, applied = change };
    }

    // Replaces the method at a position with new text, after the checks in MethodEditEngine. Plan-only returns the checks; a
    // refused plan is a reply with refused true, never an applied change. Applied writes through the VS workspace as one change set.
    public static object EditMethod(string file, int line, int column, string newMethodText, IReadOnlyList<CallerEdit> callerEdits, DocUpdates docs, bool confirm)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ThreadHelper.JoinableTaskFactory.Run(async () =>
        {
            var model = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel
                ?? throw new InvalidOperationException(Strings.Get("ComponentModelUnavailable"));
            var workspace = model.GetService<VisualStudioWorkspace>();
            var plan = await MethodEditEngine.PlanAsync(workspace.CurrentSolution, file, line, column, newMethodText, callerEdits, docs).ConfigureAwait(true);
            if (plan.Refused || !confirm)
            {
                return new { planned = !plan.Refused, applied = false, check = plan.ToReply() };
            }

            if (!workspace.TryApplyChanges(plan.Result!))
            {
                throw new InvalidOperationException(Strings.Get("MethodEditNotApplied"));
            }

            return new { planned = false, applied = true, check = plan.ToReply() };
        });
    }

    internal static async Task<(Solution Solution, Document Document, int Position)> ResolvePositionAsync(string file, int line, int column)
    {
        var model = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel
            ?? throw new InvalidOperationException(Strings.Get("ComponentModelUnavailable"));
        var solution = model.GetService<VisualStudioWorkspace>().CurrentSolution;
        var documentId = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault()
            ?? throw new ArgumentException(Strings.Format("NoRoslynDocumentFor", file));
        var document = solution.GetDocument(documentId)
            ?? throw new ArgumentException(Strings.Format("NoRoslynDocumentFor", file));

        var text = await document.GetTextAsync().ConfigureAwait(true);
        if (line < 1 || line > text.Lines.Count)
        {
            throw new ArgumentException(Strings.Format("LineOutOfRange", line, file));
        }

        var position = text.Lines[line - 1].Start + Math.Max(0, column - 1);
        return (solution, document, Math.Min(position, text.Length));
    }

    private static object ToLocation(Location location)
    {
        var span = location.GetLineSpan();
        return new
        {
            file = span.Path,
            line = span.StartLinePosition.Line + 1,
            column = span.StartLinePosition.Character + 1,
        };
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
