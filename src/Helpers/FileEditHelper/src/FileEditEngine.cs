using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AiDevHelpers.Common;

namespace AiDevHelpers.Helpers.FileEditHelper;

/// <summary>One text edit. Paths are relative to the workspace root, or absolute inside it.</summary>
public abstract record EditOperation(string FilePath, string? Description = null, bool DryRun = false);

/// <summary>Replace <paramref name="OldText" /> with <paramref name="NewText" />. Occurrence 0 replaces all; N replaces the Nth.</summary>
public sealed record ReplaceOperation(string FilePath, string OldText, string NewText, int Occurrence = 0, string? Description = null, bool DryRun = false)
    : EditOperation(FilePath, Description, DryRun);

/// <summary>Insert <paramref name="Content" /> before or after the first <paramref name="Anchor" />.</summary>
public sealed record InsertOperation(string FilePath, string Anchor, string Content, bool After = true, string? Description = null, bool DryRun = false)
    : EditOperation(FilePath, Description, DryRun);

/// <summary>Remove literal text or regex matches.</summary>
public sealed record DeleteOperation(string FilePath, string Pattern, bool IsRegex = false, string? Description = null, bool DryRun = false)
    : EditOperation(FilePath, Description, DryRun);

/// <summary>Replace an identifier. Comment-only lines (//, /*, *) are skipped unless <paramref name="ReplaceInComments" />.</summary>
public sealed record RenameOperation(string FilePath, string OldName, string NewName, bool WholeWordOnly = true, bool ReplaceInComments = false, string? Description = null, bool DryRun = false)
    : EditOperation(FilePath, Description, DryRun);

/// <summary>Outcome of an edit or a rollback.</summary>
public sealed record EditResult(bool Ok, string? Error, string FilePath, int LinesChanged = 0, bool DryRun = false, string? BackupPath = null, string? Description = null);

/// <summary>
/// Validated text edits with a byte-exact backup before every write and an atomic write that keeps the file's encoding.
/// Ported from TokenSaver's PatchEngine; see docs/Host-MCP/file-edit-contract.md.
/// </summary>
public sealed class FileEditEngine(bool requireAnchorMatch = true)
{
    /// <summary>Backup folder, relative to the workspace root.</summary>
    public const string BackupFolder = ".aidevhelpers/backups";

    public async Task<EditResult> ApplyAsync(EditOperation op, string workspaceRoot, CancellationToken ct = default)
    {
        var target = WorkspacePathGuard.Resolve(op.FilePath, workspaceRoot);
        if (!target.Ok)
            return Fail(op, target.Error!);

        var abs = target.FullPath;
        if (!File.Exists(abs))
            return Fail(op, Strings.Format("FileNotFound", op.FilePath));

        string content;
        Encoding encoding;
        try
        {
            var raw = await File.ReadAllBytesAsync(abs, ct);
            encoding = DetectEncoding(raw, out var preamble);
            content = encoding.GetString(raw, preamble, raw.Length - preamble);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(op, Strings.Format("CouldNotRead", ex.Message));
        }

        var (newContent, lines, error) = op switch
        {
            ReplaceOperation r => ApplyReplace(content, r),
            InsertOperation i => ApplyInsert(content, i),
            DeleteOperation d => ApplyDelete(content, d),
            RenameOperation n => ApplyRename(content, n),
            _ => (content, 0, Strings.Format("UnknownOperation", op.GetType().Name)),
        };
        if (error is not null)
            return Fail(op, error);

        if (op.DryRun)
            return new EditResult(true, null, op.FilePath, lines, true, null, op.Description);

        string backup;
        try
        {
            backup = await WriteBackupAsync(abs, op.FilePath, workspaceRoot, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(op, Strings.Format("BackupFailed", ex.Message));
        }

        try
        {
            await WriteAtomicAsync(abs, newContent, encoding, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(op, Strings.Format("WriteFailed", ex.Message), backup);
        }

        return new EditResult(true, null, op.FilePath, lines, false, backup, op.Description);
    }

    /// <summary>Applies operations in order and stops after the first failure.</summary>
    public async Task<IReadOnlyList<EditResult>> ApplyBatchAsync(IEnumerable<EditOperation> ops, string workspaceRoot, CancellationToken ct = default)
    {
        List<EditResult> results = [];
        foreach (var op in ops)
        {
            ct.ThrowIfCancellationRequested();
            var r = await ApplyAsync(op, workspaceRoot, ct);
            results.Add(r);
            if (!r.Ok)
                break;
        }

        return results;
    }

    /// <summary>Copies a backup over the target. The source must lie inside the backup folder and the target inside the workspace.</summary>
    public Task<EditResult> RollbackAsync(string backupPath, string targetFilePath, string workspaceRoot, CancellationToken ct = default)
    {
        var source = WorkspacePathGuard.Resolve(backupPath, workspaceRoot, BackupRoot(workspaceRoot));
        var dest = WorkspacePathGuard.Resolve(targetFilePath, workspaceRoot);
        if (!dest.Ok)
            return Task.FromResult(new EditResult(false, dest.Error, targetFilePath));

        // The source must be a backup: resolve it against the backup folder as the root, not the workspace.
        var backupOnly = WorkspacePathGuard.Resolve(backupPath, BackupRoot(workspaceRoot));
        if (!source.Ok || !backupOnly.Ok)
            return Task.FromResult(new EditResult(false, Strings.Format("NotABackup", backupPath), targetFilePath));

        if (!File.Exists(backupOnly.FullPath))
            return Task.FromResult(new EditResult(false, Strings.Format("BackupNotFound", backupPath), targetFilePath));

        try
        {
            File.Copy(backupOnly.FullPath, dest.FullPath, true);
            return Task.FromResult(new EditResult(true, null, targetFilePath, 0, false, backupOnly.FullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new EditResult(false, Strings.Format("WriteFailed", ex.Message), targetFilePath));
        }
    }

    // ── operation handlers ──

    private (string Content, int Lines, string? Error) ApplyReplace(string content, ReplaceOperation op)
    {
        if (string.IsNullOrEmpty(op.OldText))
            return (content, 0, Strings.Get("OldTextEmpty"));

        if (requireAnchorMatch && !content.Contains(op.OldText, StringComparison.Ordinal))
            return (content, 0, Strings.Format("AnchorNotFound", op.FilePath, Truncate(op.OldText, 60)));

        var lines = CountLines(op.OldText);
        if (op.Occurrence == 0)
            return (content.Replace(op.OldText, op.NewText, StringComparison.Ordinal), lines, null);

        var idx = FindNthOccurrence(content, op.OldText, op.Occurrence);
        if (idx < 0)
            return (content, 0, Strings.Format("OccurrenceNotFound", op.Occurrence, op.FilePath));

        return (content[..idx] + op.NewText + content[(idx + op.OldText.Length)..], lines, null);
    }

    private (string Content, int Lines, string? Error) ApplyInsert(string content, InsertOperation op)
    {
        if (string.IsNullOrEmpty(op.Anchor))
            return (content, 0, Strings.Get("AnchorEmpty"));

        var idx = content.IndexOf(op.Anchor, StringComparison.Ordinal);
        if (requireAnchorMatch && idx < 0)
            return (content, 0, Strings.Format("AnchorNotFound", op.FilePath, Truncate(op.Anchor, 60)));

        if (idx < 0)
            return (content + op.Content, CountLines(op.Content), null);

        var result = op.After
            ? content[..(idx + op.Anchor.Length)] + op.Content + content[(idx + op.Anchor.Length)..]
            : content[..idx] + op.Content + content[idx..];
        return (result, CountLines(op.Content), null);
    }

    private (string Content, int Lines, string? Error) ApplyDelete(string content, DeleteOperation op)
    {
        if (string.IsNullOrEmpty(op.Pattern))
            return (content, 0, Strings.Get("PatternEmpty"));

        if (op.IsRegex)
        {
            try
            {
                var result = new Regex(op.Pattern, RegexOptions.Multiline, TimeSpan.FromSeconds(2)).Replace(content, "");
                return (result, CountLines(content) - CountLines(result), null);
            }
            catch (ArgumentException ex) // RegexParseException derives from ArgumentException
            {
                return (content, 0, Strings.Format("InvalidRegex", ex.Message));
            }
            catch (RegexMatchTimeoutException)
            {
                return (content, 0, Strings.Get("RegexTimeout"));
            }
        }

        if (requireAnchorMatch && !content.Contains(op.Pattern, StringComparison.Ordinal))
            return (content, 0, Strings.Format("PatternNotFound", op.FilePath, Truncate(op.Pattern, 60)));

        return (content.Replace(op.Pattern, "", StringComparison.Ordinal), CountLines(op.Pattern), null);
    }

    private (string Content, int Lines, string? Error) ApplyRename(string content, RenameOperation op)
    {
        if (string.IsNullOrEmpty(op.OldName) || string.IsNullOrEmpty(op.NewName))
            return (content, 0, Strings.Get("NamesEmpty"));

        var pattern = op.WholeWordOnly ? $@"\b{Regex.Escape(op.OldName)}\b" : Regex.Escape(op.OldName);
        var rx = new Regex(pattern);
        int count;
        string result;

        if (op.ReplaceInComments)
        {
            count = rx.Count(content);
            result = rx.Replace(content, op.NewName);
        }
        else
        {
            count = 0;
            var sb = new StringBuilder(content.Length);
            var lines = content.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                var isComment = trimmed.StartsWith("//", StringComparison.Ordinal) ||
                                trimmed.StartsWith("/*", StringComparison.Ordinal) ||
                                trimmed.StartsWith('*');
                if (isComment)
                {
                    sb.Append(line);
                }
                else
                {
                    count += rx.Count(line);
                    sb.Append(rx.Replace(line, op.NewName));
                }

                if (i < lines.Length - 1)
                    sb.Append('\n');
            }

            result = sb.ToString();
        }

        if (requireAnchorMatch && count == 0)
            return (content, 0, Strings.Format("NameNotFound", op.OldName, op.FilePath));

        return (result, count, null);
    }

    // ── helpers ──

    private static string BackupRoot(string workspaceRoot) => Path.Combine(workspaceRoot, BackupFolder);

    private static async Task<string> WriteBackupAsync(string abs, string relPath, string workspaceRoot, CancellationToken ct)
    {
        var backupRoot = BackupRoot(workspaceRoot);
        Directory.CreateDirectory(backupRoot);

        // Millisecond timestamp plus a short guid: two edits to one file in the same second must not share a name.
        var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var unique = Guid.NewGuid().ToString("N")[..6];
        var safeName = relPath.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        var dest = Path.Combine(backupRoot, $"{ts}_{unique}_{safeName}");

        await using var src = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
        await using var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
        await src.CopyToAsync(dst, ct);
        return dest;
    }

    // BOM-aware: keeps the file's own encoding. No BOM means UTF-8 without BOM.
    private static Encoding DetectEncoding(byte[] raw, out int preambleLength)
    {
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
        {
            preambleLength = 3;
            return new UTF8Encoding(true);
        }

        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
        {
            preambleLength = 2;
            return new UnicodeEncoding(false, true);
        }

        if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
        {
            preambleLength = 2;
            return new UnicodeEncoding(true, true);
        }

        preambleLength = 0;
        return new UTF8Encoding(false);
    }

    // Temp file in the target folder, then File.Replace, so a crash never leaves a half-written file.
    private static async Task WriteAtomicAsync(string abs, string content, Encoding encoding, CancellationToken ct)
    {
        var temp = $"{abs}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await fs.WriteAsync(encoding.GetPreamble(), ct);
                await fs.WriteAsync(encoding.GetBytes(content), ct);
            }

            // Antivirus or indexers can hold the target for a few ms right after the backup read.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Replace(temp, abs, null);
                    break;
                }
                catch (IOException) when (attempt < 5)
                {
                    await Task.Delay(25 * attempt, ct);
                }
            }
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static int FindNthOccurrence(string source, string pattern, int n)
    {
        var idx = -1;
        for (var i = 0; i < n; i++)
        {
            idx = source.IndexOf(pattern, idx + 1, StringComparison.Ordinal);
            if (idx < 0)
                return -1;
        }

        return idx;
    }

    private static int CountLines(string s) => s.Count(c => c == '\n') + 1;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static EditResult Fail(EditOperation op, string error, string? backup = null) =>
        new(false, error, op.FilePath, 0, false, backup, op.Description);
}
