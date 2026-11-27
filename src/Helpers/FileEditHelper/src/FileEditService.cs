namespace AiDevHelpers.Helpers.FileEditHelper;

/// <summary>The arguments of one <c>file_edit</c> call. Only the parameters of the chosen action are read.</summary>
public sealed record FileEditRequest(
    string Action,
    string? FilePath = null,
    string? OldText = null,
    string? NewText = null,
    int Occurrence = 0,
    string? Anchor = null,
    string? Content = null,
    string? Position = null,
    string? Pattern = null,
    bool IsRegex = false,
    string? OldName = null,
    string? NewName = null,
    bool WholeWordOnly = true,
    bool ReplaceInComments = false,
    string? Description = null,
    bool DryRun = false,
    string? BackupPath = null,
    string? TargetFilePath = null);

/// <summary>Validates a <see cref="FileEditRequest" /> and runs it on a <see cref="FileEditEngine" />.</summary>
public sealed class FileEditService(FileEditEngine engine)
{
    public async Task<EditResult> ExecuteAsync(FileEditRequest r, string? root, CancellationToken ct = default)
    {
        var action = (r.Action ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(root))
            return new EditResult(false, Strings.Get("NoWorkspaceRoot"), r.FilePath ?? r.TargetFilePath ?? "");

        switch (action)
        {
            case "apply":
                return await Run(r, action, ("filePath", r.FilePath), ("oldText", r.OldText), ("newText", r.NewText),
                    () => new ReplaceOperation(r.FilePath!, r.OldText!, r.NewText!, r.Occurrence, r.Description, r.DryRun), root, ct);
            case "insert":
                var pos = (r.Position ?? "after").Trim().ToLowerInvariant();
                if (pos is not ("before" or "after") && !string.IsNullOrWhiteSpace(r.Anchor) && r.Content is not null && !string.IsNullOrWhiteSpace(r.FilePath))
                    return new EditResult(false, Strings.Format("BadPosition", r.Position ?? ""), r.FilePath);

                return await Run(r, action, ("filePath", r.FilePath), ("anchor", r.Anchor), ("content", r.Content),
                    () => new InsertOperation(r.FilePath!, r.Anchor!, r.Content!, pos != "before", r.Description, r.DryRun), root, ct);
            case "delete":
                return await Run(r, action, ("filePath", r.FilePath), ("pattern", r.Pattern), (null, null),
                    () => new DeleteOperation(r.FilePath!, r.Pattern!, r.IsRegex, r.Description, r.DryRun), root, ct);
            case "rename":
                return await Run(r, action, ("filePath", r.FilePath), ("oldName", r.OldName), ("newName", r.NewName),
                    () => new RenameOperation(r.FilePath!, r.OldName!, r.NewName!, r.WholeWordOnly, r.ReplaceInComments, r.Description, r.DryRun), root, ct);
            case "rollback":
                if (string.IsNullOrWhiteSpace(r.BackupPath))
                    return Missing("backupPath", action, r.TargetFilePath);
                if (string.IsNullOrWhiteSpace(r.TargetFilePath))
                    return Missing("targetFilePath", action, null);

                return await engine.RollbackAsync(r.BackupPath, r.TargetFilePath, root, ct);
            default:
                return new EditResult(false, Strings.Get("UnknownAction"), r.FilePath ?? "");
        }
    }

    private async Task<EditResult> Run(
        FileEditRequest r, string action, (string? Name, string? Value) a, (string? Name, string? Value) b, (string? Name, string? Value) c,
        Func<EditOperation> build, string root, CancellationToken ct)
    {
        foreach (var (name, value) in new[] { a, b, c })
        {
            // insert's content may be an empty string; the others must have text. A null value is always missing.
            if (name is null)
                continue;

            if (value is null || (name == "filePath" && string.IsNullOrWhiteSpace(value)))
                return Missing(name, action, r.FilePath);
        }

        return await engine.ApplyAsync(build(), root, ct);
    }

    private static EditResult Missing(string name, string action, string? file) =>
        new(false, Strings.Format("ParamRequired", name, action), file ?? "");
}
