using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AiDevHelpers.Common;

/// <summary>
/// The structured rules a person confirmed for a repo, from the committed <c>.aidevtools/repo-rules.json</c>. Branch names may use
/// <c>*</c> as a wildcard (<c>release/*</c>). Gate commands are the commands the repo's gate runs.
/// </summary>
public sealed record RepoRules(
    string? DefaultBranch,
    IReadOnlyList<string> ProtectedBranches,
    IReadOnlyList<string> RequiredChecks,
    string? BranchNamePattern,
    IReadOnlyList<string> GateCommands)
{
    public static readonly RepoRules Empty = new(null, [], [], null, []);

    public bool IsEmpty => DefaultBranch is null && ProtectedBranches.Count == 0 && RequiredChecks.Count == 0 && BranchNamePattern is null && GateCommands.Count == 0;
}

/// <summary>What GitHub reported for the repo on this machine (branch protection and rulesets), cached in the gitignored user file.</summary>
public sealed record LivePolicy(IReadOnlyList<string> ProtectedBranches, IReadOnlyList<string> RequiredChecks, DateTime CheckedUtc);

/// <summary>The committed rules and the cached live policy together. Both only ever add restrictions.</summary>
public sealed record EffectiveRules(RepoRules Committed, LivePolicy? Live)
{
    public IReadOnlyList<string> ProtectedBranches =>
        [.. Committed.ProtectedBranches.Concat(Live?.ProtectedBranches ?? []).Distinct(StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<string> RequiredChecks =>
        [.. Committed.RequiredChecks.Concat(Live?.RequiredChecks ?? []).Distinct(StringComparer.OrdinalIgnoreCase)];

    public bool IsEmpty => Committed.IsEmpty && (Live is null || (Live.ProtectedBranches.Count == 0 && Live.RequiredChecks.Count == 0));

    public bool IsProtected(string branch) => ProtectedBranches.Any(p => RepoRulesFile.BranchMatches(p, branch));

    /// <summary>True when there is no name pattern, or the name matches it. An unreadable pattern never refuses.</summary>
    public bool NameAllowed(string branch)
    {
        if (string.IsNullOrWhiteSpace(Committed.BranchNamePattern))
            return true;

        try
        {
            return Regex.IsMatch(branch, Committed.BranchNamePattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return true;
        }
    }
}

/// <summary>Reads and writes <c>repo-rules.json</c> and the cached live policy. A missing or malformed file means no rules and never widens anything.</summary>
public static class RepoRulesFile
{
    public const string FileName = "repo-rules.json";

    private const string LiveKey = "livePolicy";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static string FilePath(string repoRoot) => Path.Combine(repoRoot, RepoSettingsFile.FolderName, FileName);

    public static RepoRules Read(string repoRoot, List<string>? warnings = null)
    {
        var file = FilePath(repoRoot);
        if (!File.Exists(file))
            return RepoRules.Empty;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(file), documentOptions: Lenient) is not JsonObject root)
                return RepoRules.Empty;

            return new RepoRules(
                Text(root, "defaultBranch"),
                Texts(root, "protectedBranches"),
                Texts(root, "requiredChecks"),
                Text(root, "branchNamePattern"),
                Texts(root, "gateCommands"));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            warnings?.Add($"{file} is unreadable and was ignored: {ex.Message}");
            return RepoRules.Empty;
        }
    }

    /// <summary>Writes the rules, keeping any key this class does not own. A file that is not valid JSON is not overwritten.</summary>
    public static void Write(string repoRoot, RepoRules rules)
    {
        var file = FilePath(repoRoot);
        JsonObject root;
        try
        {
            root = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file), documentOptions: Lenient) as JsonObject ?? new JsonObject() : new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new IOException($"{file} is not valid JSON; fix or remove it first.", ex);
        }

        Set(root, "defaultBranch", rules.DefaultBranch);
        Set(root, "protectedBranches", rules.ProtectedBranches);
        Set(root, "requiredChecks", rules.RequiredChecks);
        Set(root, "branchNamePattern", rules.BranchNamePattern);
        Set(root, "gateCommands", rules.GateCommands);

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(Indented) + "\n");
        File.Move(temp, file, overwrite: true);
    }

    public static LivePolicy? ReadLive(string repoRoot)
    {
        try
        {
            var path = RepoSettingsFile.UserFilePath(repoRoot);
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path), documentOptions: Lenient)?[LiveKey] is not JsonObject live)
                return null;

            var checkedUtc = live["checkedUtc"] is JsonValue v && v.TryGetValue<DateTime>(out var parsed) ? parsed : DateTime.MinValue;
            return new LivePolicy(Texts(live, "protectedBranches"), Texts(live, "requiredChecks"), checkedUtc);
        }
        catch (Exception ex) when (ex is JsonException or IOException or FormatException)
        {
            return null;
        }
    }

    public static void WriteLive(string repoRoot, LivePolicy live) =>
        RepoSettingsFile.UpdateUserFile(repoRoot, root => root[LiveKey] = new JsonObject
        {
            ["protectedBranches"] = new JsonArray(live.ProtectedBranches.Select(b => (JsonNode?)JsonValue.Create(b)).ToArray()),
            ["requiredChecks"] = new JsonArray(live.RequiredChecks.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            ["checkedUtc"] = live.CheckedUtc.ToUniversalTime().ToString("O"),
        });

    public static EffectiveRules Effective(string repoRoot, List<string>? warnings = null) => new(Read(repoRoot, warnings), ReadLive(repoRoot));

    /// <summary>True when the branch name equals the pattern (ignoring case) or matches it with <c>*</c> standing for any text.</summary>
    public static bool BranchMatches(string pattern, string branch)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(branch))
            return false;

        var regex = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(branch.Trim(), regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string? Text(JsonObject root, string key) =>
        root[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static List<string> Texts(JsonObject root, string key) =>
        root[key] is JsonArray array
            ? array.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>().Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
            : [];

    private static void Set(JsonObject root, string key, string? value)
    {
        if (value is null)
            root.Remove(key);
        else
            root[key] = value;
    }

    private static void Set(JsonObject root, string key, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            root.Remove(key);
        else
            root[key] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    }
}

/// <summary>Supplies the effective rules for the repo that holds a folder. The seam keeps the source control tests off the disk.</summary>
public interface IRepoRulesSource
{
    /// <summary>The repo root that holds the folder and its effective rules, or null when the folder is not in a git repo.</summary>
    (string Root, EffectiveRules Rules)? For(string folder);
}

/// <summary>Reads the rules from the repo's own files.</summary>
public sealed class FileRepoRulesSource : IRepoRulesSource
{
    public (string Root, EffectiveRules Rules)? For(string folder)
    {
        var root = RepoSettingsFile.FindRepoRootFromFolder(folder);
        return root is null ? null : (root, RepoRulesFile.Effective(root));
    }
}

/// <summary>
/// A one-time waiver of a refused git write. Refusing a write records a request; the person grants it in a console with
/// <c>elev rules override</c> (the question comes from the console, never from tool arguments); the next matching plan uses it once.
/// This keeps an agent from waving through its own refusal by accident. It is not a defence against a process that can edit the
/// person's files directly.
/// </summary>
public static class RuleOverrides
{
    private const string Key = "ruleOverrides";
    private static readonly TimeSpan RequestLife = TimeSpan.FromHours(1);
    private static readonly TimeSpan GrantLife = TimeSpan.FromMinutes(10);

    /// <summary>Records a request for the action and branch and returns its id.</summary>
    public static string Request(string repoRoot, string action, string branch, DateTime nowUtc)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        RepoSettingsFile.UpdateUserFile(repoRoot, root =>
        {
            var list = Live(root, nowUtc);
            list.Add(new JsonObject { ["id"] = id, ["action"] = action, ["branch"] = branch, ["createdUtc"] = nowUtc.ToString("O"), ["granted"] = false });
            root[Key] = list;
        });
        return id;
    }

    /// <summary>The pending request with this id, as (action, branch), or null.</summary>
    public static (string Action, string Branch)? Find(string repoRoot, string id, DateTime nowUtc)
    {
        var entry = Entries(repoRoot, nowUtc).FirstOrDefault(e => e["id"]?.GetValue<string>() == id && e["granted"]?.GetValue<bool>() == false);
        return entry is null ? null : (entry["action"]!.GetValue<string>(), entry["branch"]!.GetValue<string>());
    }

    /// <summary>Marks the request granted. False when no such pending request exists.</summary>
    public static bool Grant(string repoRoot, string id, DateTime nowUtc)
    {
        var granted = false;
        RepoSettingsFile.UpdateUserFile(repoRoot, root =>
        {
            var list = Live(root, nowUtc);
            if (list.OfType<JsonObject>().FirstOrDefault(e => e["id"]?.GetValue<string>() == id && e["granted"]?.GetValue<bool>() == false) is { } entry)
            {
                entry["granted"] = true;
                entry["grantedUtc"] = nowUtc.ToString("O");
                granted = true;
            }

            root[Key] = list;
        });
        return granted;
    }

    /// <summary>Uses a granted, unexpired waiver for the action and branch, once. True when one was used.</summary>
    public static bool TryConsume(string repoRoot, string action, string branch, DateTime nowUtc)
    {
        var used = false;
        if (!Entries(repoRoot, nowUtc).Any(e => Matches(e, action, branch)))
            return false;

        RepoSettingsFile.UpdateUserFile(repoRoot, root =>
        {
            var list = Live(root, nowUtc);
            if (list.OfType<JsonObject>().FirstOrDefault(e => Matches(e, action, branch)) is { } entry)
            {
                list.Remove(entry);
                used = true;
            }

            root[Key] = list;
        });
        return used;
    }

    private static bool Matches(JsonObject e, string action, string branch) =>
        e["granted"]?.GetValue<bool>() == true
        && string.Equals(e["action"]?.GetValue<string>(), action, StringComparison.Ordinal)
        && string.Equals(e["branch"]?.GetValue<string>(), branch, StringComparison.OrdinalIgnoreCase);

    private static List<JsonObject> Entries(string repoRoot, DateTime nowUtc)
    {
        try
        {
            var path = RepoSettingsFile.UserFilePath(repoRoot);
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
                return [];

            return Live(root, nowUtc).OfType<JsonObject>().ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    // The entries that have not expired. Expired ones are dropped the next time the file is written.
    private static JsonArray Live(JsonObject root, DateTime nowUtc)
    {
        var kept = new JsonArray();
        if (root[Key] is JsonArray existing)
        {
            foreach (var entry in existing.OfType<JsonObject>())
            {
                var granted = entry["granted"]?.GetValue<bool>() == true;
                var stamp = entry[granted ? "grantedUtc" : "createdUtc"]?.GetValue<string>();
                if (DateTime.TryParse(stamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) && nowUtc - at.ToUniversalTime() <= (granted ? GrantLife : RequestLife))
                    kept.Add(JsonNode.Parse(entry.ToJsonString()));
            }
        }

        return kept;
    }
}
