using System.Security.Cryptography;
using System.Text.Json;

namespace AiDevHelpers.Helpers.LocalizationHelper;

/// <summary>Asks a model to complete a prompt. The MCP server implements it with client sampling; tests use a fake.</summary>
public interface ITranslator
{
    /// <summary>Returns the model's reply text.</summary>
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct);
}

/// <summary>A key waiting for translation. <see cref="Resx"/> is the culture file relative to the root; <see cref="Source"/> is the neutral text.</summary>
public sealed record TranslateItem(string Resx, string Key, string Source);

public sealed record TranslateRejection(string Key, string Reason);

public sealed record TranslateResult(IReadOnlyList<string> Written, IReadOnlyList<TranslateRejection> Rejected, IReadOnlyList<string> Files);

public sealed record TranslatePlan(string Root, string Culture, IReadOnlyList<TranslateItem> Items, IReadOnlyDictionary<string, string> Hashes, int Batches, string SystemPrompt)
{
    public string Token { get; init; } = string.Empty;

    public DateTime ExpiresUtc { get; init; }
}

/// <summary>
/// Translates the stubs (comment needs-translation) of one culture through an <see cref="ITranslator"/>. Plan sends nothing; apply sends batches,
/// validates every reply and writes only valid translations, marked machine-translated, so a person can find and review them.
/// </summary>
public sealed class TranslationService(ITranslator translator, PlanStore<TranslatePlan> store, int batchSize = TranslationService.DefaultBatchSize)
{
    public const int DefaultBatchSize = 20;
    public const string MachineTranslated = "machine-translated";

    public TranslatePlan Plan(string root, string culture, IReadOnlyList<string>? keys)
    {
        if (!ResourceSet.IsValidCulture(culture))
        {
            throw new ArgumentException(Strings.Format("InvalidCulture", culture), nameof(culture));
        }

        var fullRoot = Path.GetFullPath(root);
        var items = new List<TranslateItem>();
        foreach (var set in ResourceSet.Find(root))
        {
            if (!set.CulturePaths.TryGetValue(culture, out var path))
            {
                continue;
            }

            var neutral = ResxFile.Load(set.NeutralPath).Entries.GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
            foreach (var entry in ResxFile.Load(path).Entries.Where(e => e.Comment == CultureSyncChecker.NeedsTranslation))
            {
                if (keys is null || keys.Contains(entry.Name))
                {
                    items.Add(new TranslateItem(Path.GetRelativePath(fullRoot, path), entry.Name, neutral.GetValueOrDefault(entry.Name, entry.Value)));
                }
            }
        }

        var notStubs = keys?.Where(k => items.All(i => i.Key != k)).ToList();
        if (notStubs is { Count: > 0 })
        {
            throw new ArgumentException(Strings.Format("NotAStub", string.Join(", ", notStubs)), nameof(keys));
        }

        var hashes = items.Select(i => i.Resx).Distinct().ToDictionary(r => r, r => Hash(Path.Combine(fullRoot, r)));
        var batches = items.GroupBy(i => i.Resx).Sum(g => (int)Math.Ceiling(g.Count() / (double)batchSize));
        var plan = new TranslatePlan(fullRoot, culture, items, hashes, batches, Strings.Format("TranslatePromptSystem", culture));
        var token = store.Add(plan);
        return plan with { Token = token, ExpiresUtc = store.ExpiresUtc(token) };
    }

    public async Task<TranslateResult> ApplyAsync(string token, CancellationToken ct)
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
        var rejected = new List<TranslateRejection>();
        var files = new List<string>();
        foreach (var group in plan.Items.GroupBy(i => i.Resx))
        {
            var file = ResxFile.Load(Path.Combine(plan.Root, group.Key));
            var changed = false;
            foreach (var batch in group.Chunk(batchSize))
            {
                var replies = await Ask(plan, batch, rejected, ct);
                foreach (var item in batch)
                {
                    if (replies is null)
                    {
                        continue;
                    }

                    var problem = Validate(item, replies);
                    if (problem is not null)
                    {
                        rejected.Add(new TranslateRejection(item.Key, problem));
                        continue;
                    }

                    file.SetValue(item.Key, replies[item.Key], MachineTranslated);
                    written.Add(item.Key);
                    changed = true;
                }
            }

            if (changed)
            {
                file.Save();
                files.Add(group.Key);
            }
        }

        return new TranslateResult(written, rejected, files);
    }

    private async Task<Dictionary<string, string>?> Ask(TranslatePlan plan, TranslateItem[] batch, List<TranslateRejection> rejected, CancellationToken ct)
    {
        string reply;
        try
        {
            var user = Strings.Get("TranslatePromptItem") + "\n" + JsonSerializer.Serialize(batch.ToDictionary(i => i.Key, i => i.Source));
            reply = await translator.CompleteAsync(plan.SystemPrompt, user, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            rejected.AddRange(batch.Select(i => new TranslateRejection(i.Key, "translator-failed")));
            return null;
        }

        var parsed = Parse(reply);
        if (parsed is null)
        {
            rejected.AddRange(batch.Select(i => new TranslateRejection(i.Key, "unparseable-reply")));
        }

        return parsed;
    }

    /// <summary>The reply's string values by key, or null when it is not a JSON object. Text around the object (a code fence) is ignored.</summary>
    private static Dictionary<string, string>? Parse(string reply)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(reply[start..(end + 1)]);
            return doc.RootElement.ValueKind != JsonValueKind.Object
                ? null
                : doc.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Validate(TranslateItem item, Dictionary<string, string> replies)
    {
        if (!replies.TryGetValue(item.Key, out var text))
        {
            return "missing";
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return "empty";
        }

        if (!Placeholders.Of(item.Source).SetEquals(Placeholders.Of(text)))
        {
            return "placeholder-mismatch";
        }

        return text.Contains('\n') && !item.Source.Contains('\n') ? "unexpected-line-break" : null;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
