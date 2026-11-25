using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiDevHelpers.Common;

/// <summary>How this user wants a repo's IDE launched: only for the projects that need it, always, or never.</summary>
public enum ElevationMode
{
    Listed,
    Always,
    Never,
}

/// <summary>A project that needs elevation to run or debug. Path is repo-relative with forward slashes.</summary>
public sealed record ElevatedProject(string Path, string? Reason);

/// <summary>
/// What the repo and this user say about elevation. A missing, unknown or malformed file never raises anything: the mode stays
/// <see cref="ElevationMode.Listed"/> with no projects and a warning says what was ignored.
/// </summary>
public sealed record RepoElevation(
    ElevationMode Mode,
    IReadOnlyList<ElevatedProject> Projects,
    IReadOnlyList<string> Declined,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reads and writes the repo's elevation files. <c>.aidevhelpers/elevation.json</c> is committed and lists the projects that need
/// elevation (facts). The <c>elevation</c> block of the gitignored <c>.aidevhelpers/user.json</c> holds this user's choices (the mode
/// and the findings they declined). Neither file is read by the broker or the allowlist logic, so they can only ask for an
/// elevated launch, which the user still approves at the UAC prompt. Writers keep every key they do not own.
/// </summary>
public static class ElevationFiles
{
    public const string FileName = "elevation.json";

    private const string UserBlock = "elevation";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static string FilePath(string repoRoot) => Path.Combine(repoRoot, RepoSettingsFile.FolderName, FileName);

    /// <summary>The repo's list plus this user's choices.</summary>
    public static RepoElevation Read(string repoRoot)
    {
        var warnings = new List<string>();
        var projects = ReadProjects(repoRoot, warnings);
        var (mode, declined) = ReadUser(repoRoot, warnings);
        return new RepoElevation(mode, projects, declined, warnings);
    }

    /// <summary>True for a repo-relative path with no rooted part and no <c>..</c> segment. Such a path stays inside the repo.</summary>
    public static bool IsSafePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return false;

        return !path.Replace('\\', '/').Split('/').Any(part => part == "..");
    }

    /// <summary>The path as stored: forward slashes, no leading <c>./</c>.</summary>
    public static string Normalize(string path)
    {
        var text = path.Replace('\\', '/');
        while (text.StartsWith("./", StringComparison.Ordinal))
            text = text[2..];

        return text;
    }

    /// <summary>Adds a project to the committed list, or updates its reason. Other keys and entries are kept.</summary>
    public static void AddProject(string repoRoot, string path, string? reason)
    {
        var normalized = Normalize(path);
        if (!IsSafePath(normalized))
            throw new ArgumentException("The project path must stay inside the repo.", nameof(path));

        var path0 = FilePath(repoRoot);
        var root = ReadObject(path0) ?? new JsonObject();
        var projects = root["projects"] as JsonArray ?? new JsonArray();
        root["projects"] = projects;

        var existing = projects.OfType<JsonObject>().FirstOrDefault(p => string.Equals(p["path"]?.GetValue<string>(), normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new JsonObject { ["path"] = normalized };
            projects.Add(existing);
        }

        if (reason is not null)
            existing["reason"] = reason;

        WriteAtomic(path0, root);
    }

    /// <summary>Sets this user's mode in <c>user.json</c>.</summary>
    public static void SetMode(string repoRoot, ElevationMode mode) =>
        RepoSettingsFile.UpdateUserFile(repoRoot, root => Block(root)["mode"] = mode.ToString().ToLowerInvariant());

    /// <summary>Records that this user declined a finding, so setup does not ask again.</summary>
    public static void Decline(string repoRoot, string path)
    {
        var normalized = Normalize(path);
        RepoSettingsFile.UpdateUserFile(repoRoot, root =>
        {
            var block = Block(root);
            var declined = block["declined"] as JsonArray ?? new JsonArray();
            block["declined"] = declined;
            if (!declined.Any(d => string.Equals(d?.GetValue<string>(), normalized, StringComparison.OrdinalIgnoreCase)))
                declined.Add(normalized);
        });
    }

    private static JsonObject Block(JsonObject root)
    {
        var block = root[UserBlock] as JsonObject ?? new JsonObject();
        root[UserBlock] = block;
        return block;
    }

    private static List<ElevatedProject> ReadProjects(string repoRoot, List<string> warnings)
    {
        var file = FilePath(repoRoot);
        var projects = new List<ElevatedProject>();
        if (!File.Exists(file))
            return projects;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(file), documentOptions: Lenient);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            warnings.Add($"{file} is unreadable and was ignored: {ex.Message}");
            return projects;
        }

        if (root is not JsonObject obj || obj["projects"] is not JsonArray array)
            return projects;

        foreach (var item in array)
        {
            var path = item is JsonValue value && value.TryGetValue<string>(out var text) ? text : item?["path"]?.GetValue<string>();
            if (!IsSafePath(path))
            {
                warnings.Add($"{file}: project path '{path}' is not inside the repo and was ignored");
                continue;
            }

            var reason = item is JsonObject o && o["reason"] is JsonValue r && r.TryGetValue<string>(out var why) ? why : null;
            projects.Add(new ElevatedProject(Normalize(path!), reason));
        }

        return projects;
    }

    private static (ElevationMode Mode, List<string> Declined) ReadUser(string repoRoot, List<string> warnings)
    {
        var file = RepoSettingsFile.UserFilePath(repoRoot);
        var declined = new List<string>();
        var mode = ElevationMode.Listed;
        if (!File.Exists(file))
            return (mode, declined);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(file), documentOptions: Lenient);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            warnings.Add($"{file} is unreadable and was ignored: {ex.Message}");
            return (mode, declined);
        }

        if (root?[UserBlock] is not JsonObject block)
            return (mode, declined);

        if (block["mode"] is JsonValue m && m.TryGetValue<string>(out var text))
        {
            if (Enum.TryParse<ElevationMode>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
                mode = parsed;
            else
                warnings.Add($"{file}: unknown elevation mode '{text}' was ignored");
        }

        if (block["declined"] is JsonArray array)
        {
            declined.AddRange(array.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>().Where(IsSafePath).Select(Normalize));
        }

        return (mode, declined);
    }

    private static JsonObject? ReadObject(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: Lenient) as JsonObject;
        }
        catch (JsonException)
        {
            // A file that cannot be read is not overwritten: a malformed list must be fixed by the user.
            throw new IOException($"{path} is not valid JSON; fix or remove it first.");
        }
    }

    private static void WriteAtomic(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(Indented) + "\n");
        File.Move(temp, path, overwrite: true);
    }
}
