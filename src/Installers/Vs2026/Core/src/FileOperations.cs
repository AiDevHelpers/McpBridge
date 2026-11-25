using System;
using System.IO;
using System.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Creates, moves and deletes source files on disk for the edit tools. Only SDK-style projects are touched: they pick up files by folder,
/// so no project file needs editing. Each operation returns a reply with <c>ok</c> and, on failure, the reason. Nothing here needs the
/// UI thread.
/// </summary>
internal static class FileOperations
{
    /// <summary>True when the project file is SDK-style (its root element has an Sdk attribute, or an Sdk child, or an Sdk import).</summary>
    public static bool IsSdkStyle(string? projectPath) =>
        projectPath is not null && File.Exists(projectPath) && File.ReadAllText(projectPath).Contains("Sdk=\"");

    public static object Create(string path, string text, string? projectPath)
    {
        if (File.Exists(path))
        {
            return Failed(path, Strings.Format("FileMoveExists", path));
        }

        if (!IsSdkStyle(projectPath))
        {
            return Failed(path, Strings.Get("FileMoveNotSdk"));
        }

        return Run(path, () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        });
    }

    public static object Delete(string path, string? projectPath)
    {
        if (!File.Exists(path))
        {
            return Failed(path, Strings.Format("FileMoveMissing", path));
        }

        if (!IsSdkStyle(projectPath))
        {
            return Failed(path, Strings.Get("FileMoveNotSdk"));
        }

        return Run(path, () => File.Delete(path));
    }

    public static object Move(string from, string to, string? projectPath)
    {
        if (!File.Exists(from))
        {
            return Failed(from, Strings.Format("FileMoveMissing", from));
        }

        if (File.Exists(to))
        {
            return Failed(from, Strings.Format("FileMoveExists", to));
        }

        if (!IsSdkStyle(projectPath))
        {
            return Failed(from, Strings.Get("FileMoveNotSdk"));
        }

        return Run(from, () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to);
        }, to);
    }

    private static object Run(string path, Action action, string? to = null)
    {
        try
        {
            action();
            return to is null ? new { path, ok = true } : new { from = path, to, ok = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(path, ex.Message);
        }
    }

    private static object Failed(string path, string error) => new { path, ok = false, error };
}
