using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// One logical document is one file path. In a multi-targeted project, or a file linked into two projects, the same path is several
/// documents. An edit is planned once and applied to each of them, so every target sees the same change.
/// </summary>
internal static class LogicalDocuments
{
    /// <summary>Every document the path resolves to, across the solution's projects.</summary>
    public static IReadOnlyList<DocumentId> IdsAt(Solution solution, string path) =>
        solution.GetDocumentIdsWithFilePath(path).ToList();

    /// <summary>
    /// Applies the changes keyed by document to every document of each changed path. Edits the same path has in several documents are
    /// merged by span, so a reference found in two targets is edited once per target, not twice. When the linked copies do not have the
    /// same text before the edit, the edit is applied by position to each and <paramref name="divergent"/> lists the path.
    /// </summary>
    public static async Task<Solution> ApplyAsync(Solution solution, Dictionary<DocumentId, List<TextChange>> changes, List<string> divergent)
    {
        var byPath = new Dictionary<string, List<DocumentId>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in changes.Keys)
        {
            var path = solution.GetDocument(id)?.FilePath ?? id.ToString();
            if (!byPath.TryGetValue(path, out var ids))
            {
                byPath[path] = ids = new List<DocumentId>();
            }

            ids.Add(id);
        }

        var edited = solution;
        foreach (var pair in byPath)
        {
            var path = pair.Key;
            var ids = pair.Value;
            var targets = IdsAt(solution, path);
            if (targets.Count == 0)
            {
                targets = ids;
            }

            var merged = MergedChanges(ids.SelectMany(id => changes[id]));
            var texts = new List<SourceText>();
            foreach (var target in targets)
            {
                texts.Add(await solution.GetDocument(target)!.GetTextAsync().ConfigureAwait(false));
            }

            if (targets.Count > 1 && texts.Any(t => !t.ContentEquals(texts[0])))
            {
                divergent.Add(path);
            }

            foreach (var target in targets)
            {
                var text = await solution.GetDocument(target)!.GetTextAsync().ConfigureAwait(false);
                edited = edited.WithDocumentText(target, text.WithChanges(merged));
            }
        }

        return edited;
    }

    // Changes with the same span and text are one change; overlapping changes are kept as given and the apply will refuse them.
    private static List<TextChange> MergedChanges(IEnumerable<TextChange> changes)
    {
        var seen = new HashSet<(int Start, int Length, string Text)>();
        var list = new List<TextChange>();
        foreach (var change in changes)
        {
            if (seen.Add((change.Span.Start, change.Span.Length, change.NewText ?? "")))
            {
                list.Add(change);
            }
        }

        return list;
    }

    /// <summary>Why a path cannot be edited, or null. Generated files and read-only files are refused.</summary>
    public static string? RefusalFor(Solution solution, string path)
    {
        if (path.Replace('/', '\\').Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) || path.Replace('/', '\\').Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
        {
            return Strings.Format("LogicalGeneratedFile", path);
        }

        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
        {
            return Strings.Format("LogicalReadOnlyFile", path);
        }

        var ids = IdsAt(solution, path);
        if (ids.Count > 0 && ids.Any(id => solution.GetDocument(id)?.SourceCodeKind == SourceCodeKind.Script))
        {
            return Strings.Format("LogicalNotRegular", path);
        }

        return null;
    }
}
