using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// A unified diff of one file, for the plan reply, so the user can read a change before approving it. The diff is the span between the
/// first and last differing line (one hunk), which is what an edit tool changes; it is exact, if not minimal.
/// </summary>
internal static class UnifiedDiff
{
    public const int MaxFiles = 40;

    public const int MaxLinesPerFile = 400;

    /// <summary>The diff of one file, or null when the text did not change. Truncated after <see cref="MaxLinesPerFile"/> lines.</summary>
    public static string? Of(string path, string before, string after)
    {
        if (string.Equals(Normalize(before), Normalize(after), StringComparison.Ordinal))
        {
            return null;
        }

        var oldLines = Split(before);
        var newLines = Split(after);
        var start = 0;
        while (start < oldLines.Count && start < newLines.Count && oldLines[start] == newLines[start])
        {
            start++;
        }

        var oldEnd = oldLines.Count;
        var newEnd = newLines.Count;
        while (oldEnd > start && newEnd > start && oldLines[oldEnd - 1] == newLines[newEnd - 1])
        {
            oldEnd--;
            newEnd--;
        }

        var sb = new StringBuilder();
        sb.Append("--- a/").Append(path).Append('\n');
        sb.Append("+++ b/").Append(path).Append('\n');
        sb.Append("@@ -").Append(start + 1).Append(',').Append(oldEnd - start)
          .Append(" +").Append(start + 1).Append(',').Append(newEnd - start).Append(" @@\n");

        var lines = 0;
        foreach (var line in oldLines.Skip(start).Take(oldEnd - start))
        {
            if (++lines > MaxLinesPerFile)
            {
                return Truncated(sb);
            }

            sb.Append('-').Append(line).Append('\n');
        }

        foreach (var line in newLines.Skip(start).Take(newEnd - start))
        {
            if (++lines > MaxLinesPerFile)
            {
                return Truncated(sb);
            }

            sb.Append('+').Append(line).Append('\n');
        }

        return sb.ToString();
    }

    private static string Truncated(StringBuilder sb) => sb.Append(Strings.Format("DiffTruncatedAfter", MaxLinesPerFile)).Append('\n').ToString();

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static List<string> Split(string text) => Normalize(text).Split('\n').ToList();
}
