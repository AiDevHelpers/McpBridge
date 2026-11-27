namespace AiDevHelpers.Helpers.LocalizationHelper;

/// <summary>
/// A problem in a culture resx. <see cref="Kind"/> is "missing", "extra", "untranslated", "placeholder-mismatch" or "invalid-culture".
/// <see cref="Resx"/> is the file path relative to the checked root.
/// </summary>
public sealed record SyncFinding(string Resx, string Kind, string Key, string? Culture);

/// <summary>Compares every Strings.&lt;culture&gt;.resx with the neutral Strings.resx beside it.</summary>
public static class CultureSyncChecker
{
    public const string NeedsTranslation = "needs-translation";
    public const string SameInCulture = "same-in-culture";

    public static IReadOnlyList<SyncFinding> Check(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var findings = new List<SyncFinding>();
        foreach (var set in ResourceSet.Find(root))
        {
            findings.AddRange(set.InvalidFiles.Select(f =>
                new SyncFinding(Path.GetRelativePath(fullRoot, f), "invalid-culture", string.Empty, Path.GetFileNameWithoutExtension(f)["Strings.".Length..])));

            var neutral = ResxFile.Load(set.NeutralPath).Entries.GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var (culture, path) in set.CulturePaths)
            {
                if (string.Equals(culture, ResourceSet.PseudoCulture, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(fullRoot, path);
                var entries = ResxFile.Load(path).Entries.GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                foreach (var (name, source) in neutral)
                {
                    if (!entries.TryGetValue(name, out var translated))
                    {
                        findings.Add(new SyncFinding(relative, "missing", name, culture));
                        continue;
                    }

                    if (!Placeholders.Of(source.Value).SetEquals(Placeholders.Of(translated.Value)))
                    {
                        findings.Add(new SyncFinding(relative, "placeholder-mismatch", name, culture));
                    }

                    var stub = translated.Comment == NeedsTranslation;
                    var same = translated.Value == source.Value && translated.Comment != SameInCulture;
                    if (stub || same)
                    {
                        findings.Add(new SyncFinding(relative, "untranslated", name, culture));
                    }
                }

                findings.AddRange(entries.Keys.Where(k => !neutral.ContainsKey(k)).Select(k => new SyncFinding(relative, "extra", k, culture)));
            }
        }

        return findings
            .OrderBy(f => f.Resx, StringComparer.Ordinal)
            .ThenBy(f => f.Kind, StringComparer.Ordinal)
            .ThenBy(f => f.Key, StringComparer.Ordinal)
            .ToList();
    }
}
