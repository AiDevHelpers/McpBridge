using System;
using System.Collections.Generic;
using System.IO;
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
        string? error = null;
        if (!File.Exists(move.From))
        {
            error = Strings.Format("FileMoveMissing", move.From);
        }
        else if (File.Exists(move.To))
        {
            error = Strings.Format("FileMoveExists", move.To);
        }
        else if (move.Project is null || !File.Exists(move.Project) || !File.ReadAllText(move.Project).Contains("Sdk=\""))
        {
            error = Strings.Get("FileMoveNotSdk");
        }

        if (error is null)
        {
            try
            {
                dte?.Documents.Cast<Document>().FirstOrDefault(d => string.Equals(d.FullName, move.From, StringComparison.OrdinalIgnoreCase))?.Save();
                Directory.CreateDirectory(Path.GetDirectoryName(move.To)!);
                File.Move(move.From, move.To);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
            }
        }

        return new { from = move.From, to = move.To, ok = error is null, error };
    }
}
