using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

// The single-file edit engine behind the replaceInFile bridge action. It follows FileEditHelper's rules: a byte-exact backup
// before every write, the BOM kept, an atomic write, and a batch that stops at the first failing edit. It mirrors the VS Code
// bridge's fileEdit.ts, and the shared fixtures in conformance/ hold both to the same refusals.
public static class FileEditEngine
{
    public static class Encodings
    {
        public const string Utf8 = "utf8";
        public const string Utf16Le = "utf16le";
        public const string Utf16Be = "utf16be";
    }

    public readonly record struct BomInfo(string Encoding, int BomLength);

    public readonly record struct Edit(string OldText, string NewText, int Occurrence);

    public readonly record struct ApplyResult(string Text, int Matches);

    public readonly record struct ReplaceResult(bool Applied, string File, int Matches, int LinesChanged, string? BackupPath);

    // Reads the byte order mark. No BOM means UTF-8.
    public static BomInfo DetectBom(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new BomInfo(Encodings.Utf8, 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return new BomInfo(Encodings.Utf16Le, 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return new BomInfo(Encodings.Utf16Be, 2);
        }

        return new BomInfo(Encodings.Utf8, 0);
    }

    // Applies the edits in order. Each edit needs at least one match. occurrence 0 replaces every match, N replaces only the Nth.
    public static ApplyResult ApplyEdits(string text, IReadOnlyList<Edit> edits)
    {
        var current = text;
        var total = 0;
        foreach (var edit in edits)
        {
            if (string.IsNullOrEmpty(edit.OldText))
            {
                throw new InvalidOperationException(Strings.Get("FileEditEmptyOldText"));
            }

            var positions = new List<int>();
            for (var from = 0; ;)
            {
                var at = current.IndexOf(edit.OldText, from, StringComparison.Ordinal);
                if (at < 0)
                {
                    break;
                }

                positions.Add(at);
                from = at + edit.OldText.Length;
            }

            if (positions.Count == 0)
            {
                throw new InvalidOperationException(Strings.Format("FileEditNoMatch", edit.OldText));
            }

            total += positions.Count;
            var chosen = edit.Occurrence == 0
                ? positions
                : edit.Occurrence <= positions.Count
                    ? new List<int> { positions[edit.Occurrence - 1] }
                    : new List<int>();
            if (chosen.Count == 0)
            {
                throw new InvalidOperationException(Strings.Format("FileEditOccurrenceMissing", positions.Count, edit.Occurrence));
            }

            // Replace from the end so earlier positions stay valid.
            for (var i = chosen.Count - 1; i >= 0; i--)
            {
                var at = chosen[i];
                current = current.Substring(0, at) + edit.NewText + current.Substring(at + edit.OldText.Length);
            }
        }

        return new ApplyResult(current, total);
    }

    // Plans or applies one file's edits. A plan writes nothing. An apply writes a byte-exact backup first, then the new text atomically.
    public static ReplaceResult Replace(string root, string file, IReadOnlyList<Edit> edits, bool confirm)
    {
        var resolvedRoot = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(resolvedRoot, file));
        if (!IsInside(resolvedRoot, target))
        {
            throw new InvalidOperationException(Strings.Format("FileEditOutsideRoot", file));
        }

        var relative = RelativeTo(resolvedRoot, target);
        RefuseLinks(resolvedRoot, target, relative);
        if (!File.Exists(target))
        {
            throw new InvalidOperationException(Strings.Format("FileEditNotFound", relative));
        }

        var original = File.ReadAllBytes(target);
        var bom = DetectBom(original);
        var body = original.Skip(bom.BomLength).ToArray();
        var decoded = Decode(body, bom.Encoding);
        var result = ApplyEdits(decoded, edits);

        if (!confirm)
        {
            return new ReplaceResult(false, relative, result.Matches, 0, null);
        }

        var backupDir = Path.Combine(resolvedRoot, ".aidevhelpers", "backups");
        Directory.CreateDirectory(backupDir);
        var backupPath = Path.Combine(backupDir, BackupName(relative));
        File.WriteAllBytes(backupPath, original);

        var encoded = Encode(result.Text, bom.Encoding);
        var prefix = original.Take(bom.BomLength).ToArray();
        var temp = target + "." + RandomHex(4) + ".tmp";
        File.WriteAllBytes(temp, prefix.Concat(encoded).ToArray());
        // The target exists (checked above), so File.Replace swaps the new file in atomically, as .NET Framework has no overwrite flag on Move.
        File.Replace(temp, target, null);

        return new ReplaceResult(true, relative, result.Matches, CountChangedLines(decoded, result.Text), backupPath);
    }

    private static string Decode(byte[] body, string encoding) => encoding switch
    {
        Encodings.Utf16Le => Encoding.Unicode.GetString(body),
        Encodings.Utf16Be => Encoding.BigEndianUnicode.GetString(body),
        _ => new UTF8Encoding(false).GetString(body),
    };

    private static byte[] Encode(string text, string encoding) => encoding switch
    {
        Encodings.Utf16Le => Encoding.Unicode.GetBytes(text),
        Encodings.Utf16Be => Encoding.BigEndianUnicode.GetBytes(text),
        _ => new UTF8Encoding(false).GetBytes(text),
    };

    // True when target is root or inside it, comparing full paths.
    private static bool IsInside(string root, string target)
    {
        var rel = RelativeTo(root, target);
        return rel == "." || (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel));
    }

    // Path.GetRelativePath is not in .NET Framework. target is assumed to be a full path; the result is "." for the root itself.
    private static string RelativeTo(string root, string target)
    {
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(trimmedRoot, target, StringComparison.OrdinalIgnoreCase))
        {
            return ".";
        }

        var prefix = trimmedRoot + Path.DirectorySeparatorChar;
        return target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? target.Substring(prefix.Length)
            : "..";
    }

    private static string RandomHex(int bytes) => Guid.NewGuid().ToString("N").Substring(0, bytes * 2);

    // A reparse point (symlink or junction) on the way to the file, including the file itself, refuses the edit.
    private static void RefuseLinks(string root, string target, string relative)
    {
        for (var current = target; current != null && IsInside(root, current) && current != root; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(Strings.Format("FileEditLink", relative));
            }
        }
    }

    private static string BackupName(string relative)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture);
        var safe = string.Concat(relative.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar ? '_' : c));
        return $"{stamp}_{RandomHex(3)}_{safe}";
    }

    private static int CountChangedLines(string before, string after)
    {
        var a = before.Split('\n');
        var b = after.Split('\n');
        var changed = Math.Abs(a.Length - b.Length);
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] != b[i])
            {
                changed++;
            }
        }

        return changed;
    }
}
