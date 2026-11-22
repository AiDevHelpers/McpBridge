using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using EnvDTE;
using EnvDTE80;

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Visual Studio bookmarks for the bridge (D5, D17). Native bookmark markers are read from the text buffers of open
/// documents (the Running Document Table), as Bookmark Studio does. Bookmarks in closed files are not visible.
/// A bookmark id is "file|line". Names are not stored by Visual Studio, so name is always null here. Runs on the UI thread.
/// </summary>
internal static class BookmarkActions
{
    private const int MarkerBookmark = 3;          // MARKERTYPE.MARKER_BOOKMARK
    private const int MarkerBookmarkDisabled = 21; // MARKERTYPE2.MARKER_BOOKMARK_DISABLED
    private const uint EmGlyphInSpan = 16;         // ENUMMARKERFLAGS.EM_GLYPHINSPAN

    public static object List(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var items = new List<object>();
        foreach (var path in OpenDocumentPaths(dte))
        {
            var lines = TextLinesFor(path);
            if (lines is null)
            {
                continue;
            }

            var lineCount = LineCount(lines);
            foreach (var line in BookmarkLines(lines, 0, lineCount - 1))
            {
                items.Add(Project(path, line));
            }
        }

        return items;
    }

    public static object? Get(string id)
    {
        var (path, line) = ParseId(id);
        return HasBookmark(path, line) ? Project(path, line) : null;
    }

    public static object Set(DTE2 dte, string file, int line)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!HasBookmark(file, line))
        {
            dte.ItemOperations.OpenFile(file);
            ((TextSelection)dte.ActiveDocument.Selection).GotoLine(line, true);
            dte.ExecuteCommand("Edit.ToggleBookmark");
        }

        if (!HasBookmark(file, line))
        {
            throw new InvalidOperationException(Strings.Format("BookmarkNotPlaced", file, line));
        }

        return Project(file, line);
    }

    public static bool Remove(string id)
    {
        var (path, line) = ParseId(id);
        var markers = MarkersOnLine(path, line).ToList();
        foreach (var marker in markers)
        {
            marker.Invalidate();
        }

        return markers.Count > 0;
    }

    private static bool HasBookmark(string path, int line) => MarkersOnLine(path, line).Any();

    private static IEnumerable<IVsTextLineMarker> MarkersOnLine(string path, int line)
    {
        var lines = TextLinesFor(path);
        if (lines is null)
        {
            return [];
        }

        return MarkersBetween(lines, line - 1, line - 1)
            .Where(m => StartLine(m) == line - 1);
    }

    private static IEnumerable<int> BookmarkLines(IVsTextLines lines, int fromLine, int toLine) =>
        MarkersBetween(lines, fromLine, toLine)
            .Select(StartLine)
            .Distinct()
            .OrderBy(l => l)
            .Select(l => l + 1); // 1-based for the bridge

    private static IEnumerable<IVsTextLineMarker> MarkersBetween(IVsTextLines lines, int fromLine, int toLine)
    {
        foreach (var markerType in new[] { MarkerBookmark, MarkerBookmarkDisabled })
        {
            var hr = lines.EnumMarkers(fromLine, 0, toLine, 0, markerType, EmGlyphInSpan, out IVsEnumLineMarkers enumerator);
            if (hr != VSConstants.S_OK || enumerator is null)
            {
                continue;
            }

            while (enumerator.Next(out IVsTextLineMarker marker) == VSConstants.S_OK && marker is not null)
            {
                yield return marker;
            }
        }
    }

    // Zero-based start line of a marker.
    private static int StartLine(IVsTextLineMarker marker)
    {
        var span = new TextSpan[1];
        marker.GetCurrentSpan(span);
        return span[0].iStartLine;
    }

    private static int LineCount(IVsTextLines lines)
    {
        lines.GetLineCount(out var count);
        return count;
    }

    private static IEnumerable<string> OpenDocumentPaths(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var paths = new List<string>();
        foreach (Document document in dte.Documents)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var path = document.FullName;
            if (!string.IsNullOrEmpty(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    private static IVsTextLines? TextLinesFor(string path)
    {
        var rdt = ServiceProvider.GetGlobalService<SVsRunningDocumentTable, IVsRunningDocumentTable>();
        var hr = rdt.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, path, out _, out _, out IntPtr docData, out _);
        if (hr != VSConstants.S_OK || docData == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.GetObjectForIUnknown(docData) as IVsTextLines;
        }
        finally
        {
            Marshal.Release(docData);
        }
    }

    private static (string Path, int Line) ParseId(string id)
    {
        var bar = id.LastIndexOf('|');
        if (bar <= 0 || !int.TryParse(id.Substring(bar + 1), out var line))
        {
            throw new ArgumentException(Strings.Get("BookmarkIdFormat"));
        }

        return (id.Substring(0, bar), line);
    }

    private static object Project(string path, int line) => new
    {
        id = $"{path}|{line}",
        file = path,
        line,
        name = (string?)null,
    };
}
