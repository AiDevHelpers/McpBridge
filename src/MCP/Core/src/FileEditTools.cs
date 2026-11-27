using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Common;
using AiDevHelpers.Helpers.FileEditHelper;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Plain-text file edits confined to a workspace root, with a backup before every write.</summary>
[McpServerToolType]
internal sealed class FileEditTools
{
    private static readonly FileEditService Service = new(new FileEditEngine());

    [McpServerTool]
    [Description(
        "Apply, insert, delete, rename, or roll back a text edit in a file inside the workspace root. A byte-exact backup is written to " +
        ".aidevhelpers/backups before every write; dryRun=true validates without writing. The file's encoding (BOM) is kept. action:\n" +
        "  apply    - replace oldText with newText (occurrence 0 = all, N = the Nth match).\n" +
        "  insert   - insert content before or after an anchor (position 'before' or 'after', default 'after').\n" +
        "  delete   - remove pattern (isRegex=true for a regex).\n" +
        "  rename   - replace identifier oldName with newName (wholeWordOnly, replaceInComments).\n" +
        "  rollback - copy a backup (backupPath, from an earlier edit) over targetFilePath.\n" +
        "root defaults to the git repo of the server's current folder.")]
    public static async Task<string> FileEdit(
        [Description("'apply', 'insert', 'delete', 'rename' or 'rollback'")] string action,
        [Description("File to edit, relative to root (all actions except rollback)")] string? filePath = null,
        [Description("apply: exact text to replace")] string? oldText = null,
        [Description("apply: replacement text")] string? newText = null,
        [Description("apply: 0 = all matches (default), N = the Nth")] int occurrence = 0,
        [Description("insert: anchor text")] string? anchor = null,
        [Description("insert: text to insert")] string? content = null,
        [Description("insert: 'before' or 'after' (default 'after')")] string? position = null,
        [Description("delete: text or regex to remove")] string? pattern = null,
        [Description("delete: treat pattern as a regex")] bool isRegex = false,
        [Description("rename: identifier to replace")] string? oldName = null,
        [Description("rename: replacement identifier")] string? newName = null,
        [Description("rename: whole words only (default true)")] bool wholeWordOnly = true,
        [Description("rename: also rename on comment lines")] bool replaceInComments = false,
        [Description("Optional description of the change")] string? description = null,
        [Description("Validate only; write nothing")] bool dryRun = false,
        [Description("rollback: backup path returned by an earlier edit")] string? backupPath = null,
        [Description("rollback: file to restore, relative to root")] string? targetFilePath = null,
        [Description("Workspace root folder (default: the git repo of the current folder)")] string? root = null,
        CancellationToken ct = default)
    {
        var effectiveRoot = string.IsNullOrWhiteSpace(root)
            ? RepoSettingsFile.FindRepoRootFromFolder(Directory.GetCurrentDirectory())
            : root;

        var request = new FileEditRequest(action, filePath, oldText, newText, occurrence, anchor, content, position, pattern, isRegex,
            oldName, newName, wholeWordOnly, replaceInComments, description, dryRun, backupPath, targetFilePath);
        var r = await Service.ExecuteAsync(request, effectiveRoot, ct);

        return r.Ok
            ? JsonSerializer.Serialize(new { ok = true, filePath = r.FilePath, linesChanged = r.LinesChanged, dryRun = r.DryRun, backupPath = r.BackupPath, description = r.Description })
            : JsonSerializer.Serialize(new { ok = false, error = r.Error });
    }
}
