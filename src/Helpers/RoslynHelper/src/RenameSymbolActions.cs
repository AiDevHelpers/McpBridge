using System.Text.Json;
using AiDevHelpers.Installers.Vs2026.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;

namespace AiDevHelpers.Helpers.RoslynHelper;

/// <summary>
/// renameSymbol: renames the symbol at a position and every reference to it, in every project. Without confirm it reports what
/// would change; with confirm it writes the files through <see cref="PlanApplier"/>. Same reply shape as the VS bridge.
/// </summary>
public static class RenameSymbolActions
{
    public static void Register(ActionDispatcher dispatcher, Func<Solution?> current, Action<Solution> setCurrent) =>
        dispatcher.Register("renameSymbol", args => RenameAsync(args, current, setCurrent));

    private static async Task<object> RenameAsync(JsonElement args, Func<Solution?> current, Action<Solution> setCurrent)
    {
        var solution = current() ?? throw new InvalidOperationException("No solution is loaded. Call loadSolution first.");
        var file = ArgumentParsers.RequireString(args, "file");
        var line = ArgumentParsers.RequireInt(args, "line");
        var column = ArgumentParsers.OptionalInt(args, "column") ?? 1;
        var newName = ArgumentParsers.RequireString(args, "newName");
        var confirm = ArgumentParsers.OptionalBool(args, "confirm") == true;

        var documentId = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault()
            ?? throw new InvalidOperationException($"File is not in the loaded solution: {file}.");
        var document = solution.GetDocument(documentId)!;
        var text = await document.GetTextAsync().ConfigureAwait(false);
        if (line < 1 || line > text.Lines.Count)
        {
            throw new ArgumentException($"Line {line} is outside {file}.");
        }

        var position = text.Lines[line - 1].Start + column - 1;
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position).ConfigureAwait(false)
            ?? throw new ArgumentException("No symbol at that position.");

        var renamed = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName).ConfigureAwait(false);
        var files = new List<string>();
        foreach (var projectChanges in renamed.GetChanges(solution).GetProjectChanges())
        {
            foreach (var changed in projectChanges.GetChangedDocuments())
            {
                if (renamed.GetDocument(changed)?.FilePath is { } path)
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

        var result = await PlanApplier.ApplyAsync(solution, renamed).ConfigureAwait(false);
        if (!result.Applied)
        {
            throw new InvalidOperationException(result.Refusal ?? "The rename was not applied.");
        }

        setCurrent(renamed);
        return new { planned = false, applied = change };
    }
}
