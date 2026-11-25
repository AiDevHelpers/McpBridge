using System;
using System.Collections.Generic;

using System.Linq;
using EnvDTE;
using Microsoft.VisualStudio.Shell;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Moves source files on disk after a namespace change, so folders match namespaces. Only SDK-style projects, which pick files up by
/// folder, are handled; any other project is told to move the file in Solution Explorer. Open documents are saved first.
/// </summary>
internal static class FileMover
{
    public static List<object> Apply(IEnumerable<FileMove> moves)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dte = ServiceProvider.GlobalProvider.GetService(typeof(DTE)) as DTE;
        var results = new List<object>();
        foreach (var move in moves)
        {
            results.Add(Apply(move, dte));
        }

        return results;
    }

    private static object Apply(FileMove move, DTE? dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // An open copy is saved first so the move does not lose unsaved text.
        if (File.Exists(move.From))
        {
            dte?.Documents.Cast<Document>().FirstOrDefault(d => string.Equals(d.FullName, move.From, StringComparison.OrdinalIgnoreCase))?.Save();
        }

        return FileOperations.Move(move.From, move.To, move.Project);
    }
}
