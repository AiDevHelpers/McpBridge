using System.Security.Cryptography;

namespace AiDevHelpers.Helpers.LocalizationHelper;

/// <summary>One change a sync would make. <see cref="Action"/> is "create-file", "add-stub" or "remove-extra".</summary>
public sealed record SyncChange(string Resx, string Action, string Key, string Culture);

/// <summary>A planned sync. <see cref="Hashes"/> maps each touched path (relative to the root) to its SHA-256, or empty when the file does not exist yet.</summary>
public sealed record SyncPlan(string Root, IReadOnlyList<SyncChange> Changes, IReadOnlyDictionary<string, string> Hashes)
{
    public string Token { get; init; } = string.Empty;

    public DateTime ExpiresUtc { get; init; }
}

/// <summary>Plans and applies a sync of culture resx files with the neutral one. Nothing is written until <see cref="Apply"/>.</summary>
public sealed class CultureSyncWriter(PlanStore<SyncPlan> store)
{
    /// <param name="culture">One culture to sync (created when it has no file yet), or null for every existing culture file.</param>
    /// <param name="removeExtra">Also remove keys that are not in the neutral file.</param>
    public SyncPlan Plan(string root, string? culture, bool removeExtra)
    {
        if (culture is not null && !ResourceSet.IsValidCulture(culture))
        {
            throw new ArgumentException(Strings.Format("InvalidCulture", culture), nameof(culture));
        }

        var fullRoot = Path.GetFullPath(root);
        var changes = new List<SyncChange>();
        foreach (var set in ResourceSet.Find(root))
        {
            var neutral = ResxFile.Load(set.NeutralPath).Entries;
            var neutralNames = neutral.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            var targets = culture is null
                ? set.CulturePaths.Where(c => !string.Equals(c.Key, ResourceSet.PseudoCulture, StringComparison.OrdinalIgnoreCase)).Select(c => (c.Key, c.Value)).ToList()
                : [(culture, set.CulturePaths.TryGetValue(culture, out var existing) ? existing : Path.Combine(set.Dir, $"Strings.{culture}.resx"))];

            foreach (var (name, path) in targets)
            {
                var relative = Path.GetRelativePath(fullRoot, path);
                var present = File.Exists(path)
                    ? ResxFile.Load(path).Entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal)
                    : [];
                if (!File.Exists(path))
                {
                    changes.Add(new SyncChange(relative, "create-file", string.Empty, name));
                }

                changes.AddRange(neutral.Where(e => !present.Contains(e.Name)).Select(e => new SyncChange(relative, "add-stub", e.Name, name)));
                if (removeExtra)
                {
                    changes.AddRange(present.Where(k => !neutralNames.Contains(k)).Order(StringComparer.Ordinal).Select(k => new SyncChange(relative, "remove-extra", k, name)));
                }
            }
        }

        var hashes = changes
            .Select(c => c.Resx)
            .Distinct()
            .Concat(ResourceSet.Find(root).Select(s => Path.GetRelativePath(fullRoot, s.NeutralPath)))
            .Distinct()
            .ToDictionary(r => r, r => Hash(Path.Combine(fullRoot, r)));
        var plan = new SyncPlan(fullRoot, changes, hashes);
        var token = store.Add(plan);
        return plan with { Token = token, ExpiresUtc = store.ExpiresUtc(token) };
    }

    /// <summary>Applies a plan once. Returns the files written, relative to the root.</summary>
    public IReadOnlyList<string> Apply(string token)
    {
        if (!store.TryTake(token, out var plan))
        {
            throw new InvalidOperationException(Strings.Get("TokenInvalid"));
        }

        foreach (var (relative, hash) in plan.Hashes)
        {
            if (Hash(Path.Combine(plan.Root, relative)) != hash)
            {
                throw new InvalidOperationException(Strings.Format("FileChangedSincePlan", relative));
            }
        }

        var written = new List<string>();
        foreach (var group in plan.Changes.GroupBy(c => c.Resx))
        {
            var path = Path.Combine(plan.Root, group.Key);
            ResxFile file;
            if (File.Exists(path))
            {
                file = ResxFile.Load(path);
            }
            else
            {
                var neutralPath = Path.Combine(Path.GetDirectoryName(path)!, "Strings.resx");
                file = ResxFile.CreateFrom(ResxFile.Load(neutralPath), path);
            }

            var neutralValues = ResxFile.Load(Path.Combine(Path.GetDirectoryName(path)!, "Strings.resx")).Entries.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
            foreach (var change in group)
            {
                switch (change.Action)
                {
                    case "add-stub":
                        file.Add(change.Key, neutralValues[change.Key], CultureSyncChecker.NeedsTranslation);
                        break;
                    case "remove-extra":
                        file.Remove(change.Key);
                        break;
                }
            }

            file.Save();
            written.Add(group.Key);
        }

        return written;
    }

    private static string Hash(string path) =>
        File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : string.Empty;
}
