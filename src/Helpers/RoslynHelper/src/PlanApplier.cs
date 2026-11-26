using System.Text;
using Microsoft.CodeAnalysis;

namespace AiDevHelpers.Helpers.RoslynHelper;

/// <summary>The outcome of applying a plan: the files written, or the reason nothing was written.</summary>
public sealed record ApplyResult(bool Applied, IReadOnlyList<string> Written, string? Refusal);

/// <summary>
/// Writes a plan's changed documents to disk. The helper never uses the workspace's TryApplyChanges or the editor, so a plan
/// lands on disk only. Before any write, every changed file must still hold the text the plan was made from; if one does
/// not, nothing is written.
/// </summary>
public static class PlanApplier
{
    public static async Task<ApplyResult> ApplyAsync(Solution snapshot, Solution planned, CancellationToken cancellationToken = default)
    {
        var writes = new List<(string Path, string Text, Encoding Encoding)>();
        foreach (var documentId in planned.GetChanges(snapshot).GetProjectChanges().SelectMany(p => p.GetChangedDocuments()))
        {
            var before = snapshot.GetDocument(documentId);
            var after = planned.GetDocument(documentId);
            if (before?.FilePath is null)
            {
                return Refused($"Changed document has no file on disk: {documentId}.");
            }

            if (after is null)
            {
                return Refused($"Plan removes {before.FilePath}; the helper does not delete files in this step.");
            }

            var beforeText = await before.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var afterText = await after.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (!File.Exists(before.FilePath) || File.ReadAllText(before.FilePath) != beforeText.ToString())
            {
                return Refused($"File changed on disk since the plan was made: {before.FilePath}.");
            }

            writes.Add((before.FilePath, afterText.ToString(), beforeText.Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
        }

        foreach (var (path, text, encoding) in writes)
        {
            File.WriteAllText(path, text, encoding);
        }

        return new ApplyResult(true, writes.Select(w => w.Path).ToList(), null);
    }

    private static ApplyResult Refused(string reason) => new(false, Array.Empty<string>(), reason);
}
