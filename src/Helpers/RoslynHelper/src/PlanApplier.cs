using System.Text;
using Microsoft.CodeAnalysis;

namespace AiDevHelpers.Helpers.RoslynHelper;

/// <summary>The outcome of applying a plan: the files written, or the reason nothing was written.</summary>
public sealed record ApplyResult(bool Applied, IReadOnlyList<string> Written, string? Refusal, IReadOnlyList<UnsavedEdit>? Unsaved = null);

/// <summary>The planned text of a file that has unsaved editor changes. The helper does not write it; the editor applies it.</summary>
public sealed record UnsavedEdit(string Path, string Text);

/// <summary>
/// Writes a plan's changed documents to disk. The helper never uses the workspace's TryApplyChanges or the editor, so a plan
/// lands on disk only. Before any write, every changed file must still hold the text the plan was made from; if one does
/// not, nothing is written.
/// </summary>
public static class PlanApplier
{
    /// <summary>
    /// Writes the plan's text changes, adds its new files, and checks its removed files. <paramref name="allowedDeletes"/> lists the
    /// files the plan deletes; a removed document outside that list refuses the whole plan. The caller deletes and moves the files
    /// after this returns applied.
    /// </summary>
    public static async Task<ApplyResult> ApplyAsync(
        Solution snapshot, Solution planned, IReadOnlyCollection<string>? allowedDeletes = null, IReadOnlySet<string>? dirtyPaths = null,
        CancellationToken cancellationToken = default)
    {
        var writes = new List<(string Path, string Text, Encoding Encoding)>();
        var unsaved = new List<UnsavedEdit>();
        foreach (var documentId in planned.GetChanges(snapshot).GetProjectChanges().SelectMany(p => p.GetChangedDocuments()))
        {
            var before = snapshot.GetDocument(documentId);
            var after = planned.GetDocument(documentId);
            if (before?.FilePath is null)
            {
                return Refused($"Changed document has no file on disk: {documentId}.");
            }

            var afterText = await after.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (dirtyPaths?.Contains(before.FilePath) == true)
            {
                // The editor holds unsaved text for this file: its planned text goes back in the reply, and the disk is left alone.
                unsaved.Add(new UnsavedEdit(before.FilePath, afterText.ToString()));
                continue;
            }

            var beforeText = await before.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (!File.Exists(before.FilePath) || File.ReadAllText(before.FilePath) != beforeText.ToString())
            {
                return Refused(Strings.Format("FileChangedOnDisk", before.FilePath));
            }

            writes.Add((before.FilePath, afterText.ToString(), beforeText.Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
        }

        foreach (var projectChange in planned.GetChanges(snapshot).GetProjectChanges())
        {
            foreach (var documentId in projectChange.GetRemovedDocuments())
            {
                var removed = snapshot.GetDocument(documentId);
                var path = removed?.FilePath;
                if (path is null || allowedDeletes is null || !allowedDeletes.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    return Refused(Strings.Format("PlanRemovesUnlisted", path ?? documentId.ToString()));
                }

                if (dirtyPaths?.Contains(path) == true)
                {
                    return Refused(Strings.Format("PlanRemovesUnsaved", path));
                }

                var removedText = await removed!.GetTextAsync(cancellationToken).ConfigureAwait(false);
                if (!File.Exists(path) || File.ReadAllText(path) != removedText.ToString())
                {
                    return Refused(Strings.Format("FileChangedOnDisk", path));
                }
            }

            foreach (var documentId in projectChange.GetAddedDocuments())
            {
                var added = planned.GetDocument(documentId);
                var path = added?.FilePath;
                if (added is null || path is null)
                {
                    return Refused(Strings.Format("AddedNoPath", documentId));
                }

                if (File.Exists(path))
                {
                    return Refused(Strings.Format("PlanAddsExisting", path));
                }

                var addedText = await added.GetTextAsync(cancellationToken).ConfigureAwait(false);
                writes.Add((path, addedText.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
            }
        }

        foreach (var (path, text, encoding) in writes)
        {
            File.WriteAllText(path, text, encoding);
        }

        return new ApplyResult(true, writes.Select(w => w.Path).ToList(), null, unsaved);
    }

    private static ApplyResult Refused(string reason) => new(false, Array.Empty<string>(), reason);
}
