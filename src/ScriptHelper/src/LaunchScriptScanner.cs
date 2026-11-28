using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// The launch profile, configuration or task that runs a script. Project is the repo-relative folder of the project that owns a
/// launchSettings profile, or null for a VS Code configuration or task.
/// </summary>
public sealed record ScriptOwner(string Name, string? Project);

/// <summary>A script a launch or debug configuration runs. Listed means it already matches the repo allowlist.</summary>
public sealed record FoundScript(string Path, string Source, bool Listed, IReadOnlyList<ScriptOwner>? Owners = null);

/// <summary>
/// Looks through a repo's launch and debug configuration (<c>Properties/launchSettings.json</c>, <c>.vscode/launch.json</c>,
/// <c>.vscode/tasks.json</c>) for scripts they run, so the user can be offered to add them to the allowlist. Only scripts that
/// exist inside the repo are reported. Reads only.
/// </summary>
public static class LaunchScriptScanner
{
    private static readonly string[] ScriptExtensions = [".ps1", ".cmd", ".bat", ".sh"];

    /// <summary>The deepest folder level (the repo root is 0) that is searched for configuration files.</summary>
    public const int MaxDepth = RepoWalk.MaxDepth;

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly Regex Token = new("\"([^\"]*)\"|'([^']*)'|(\\S+)", RegexOptions.CultureInvariant);

    public static IReadOnlyList<FoundScript> Scan(string root, IReadOnlyList<AllowlistEntry> listed)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var found = new Dictionary<string, FoundScript>(StringComparer.OrdinalIgnoreCase);

        foreach (var (file, kind) in ConfigFiles(fullRoot))
        {
            var baseDirs = kind == Kind.LaunchSettings
                ? new[] { Path.GetDirectoryName(Path.GetDirectoryName(file))!, fullRoot }
                : [fullRoot];

            var project = kind == Kind.LaunchSettings
                ? Path.GetRelativePath(fullRoot, baseDirs[0]).Replace('\\', '/')
                : null;

            foreach (var (value, name) in Values(file, kind))
            {
                foreach (var candidate in ScriptTokens(value))
                {
                    var rel = Resolve(fullRoot, baseDirs, candidate);
                    if (rel is null)
                        continue;

                    var owner = new ScriptOwner(name, project);
                    if (found.TryGetValue(rel, out var existing))
                    {
                        var owners = existing.Owners ?? [];
                        if (!owners.Contains(owner))
                            found[rel] = existing with { Owners = [.. owners, owner] };

                        continue;
                    }

                    found[rel] = new FoundScript(rel, Path.GetRelativePath(fullRoot, file).Replace('\\', '/'), Allowlist.IsPathListed(listed, rel), [owner]);
                }
            }
        }

        return found.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    private enum Kind
    {
        LaunchSettings,
        VsCodeLaunch,
        VsCodeTasks,
    }

    private static IEnumerable<(string File, Kind Kind)> ConfigFiles(string root)
    {
        foreach (var (dir, file) in RepoWalk.Files(root))
        {
            var name = Path.GetFileName(dir);
            var fileName = Path.GetFileName(file);
            if (string.Equals(name, "Properties", StringComparison.OrdinalIgnoreCase) && string.Equals(fileName, "launchSettings.json", StringComparison.OrdinalIgnoreCase))
                yield return (file, Kind.LaunchSettings);
            else if (string.Equals(name, ".vscode", StringComparison.OrdinalIgnoreCase) && string.Equals(fileName, "launch.json", StringComparison.OrdinalIgnoreCase))
                yield return (file, Kind.VsCodeLaunch);
            else if (string.Equals(name, ".vscode", StringComparison.OrdinalIgnoreCase) && string.Equals(fileName, "tasks.json", StringComparison.OrdinalIgnoreCase))
                yield return (file, Kind.VsCodeTasks);
        }
    }

    // The strings in a config file that can name a program or a script argument.
    private static IEnumerable<(string Value, string Name)> Values(string file, Kind kind)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(file), Lenient);
        }
        catch (JsonException)
        {
            yield break;
        }
        catch (IOException)
        {
            yield break;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                yield break;

            IEnumerable<(string Name, JsonElement Item)> items = kind switch
            {
                Kind.LaunchSettings => root.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Object
                    ? profiles.EnumerateObject().Select(p => (p.Name, p.Value))
                    : [],
                Kind.VsCodeLaunch => Array(root, "configurations").Select(c => (TextOf(c, "name"), c)),
                _ => Array(root, "tasks").Select(t => (TextOf(t, "label"), t)),
            };

            foreach (var (owner, item) in items.Where(i => i.Item.ValueKind == JsonValueKind.Object))
            {
                foreach (var name in new[] { "executablePath", "commandLineArgs", "program", "command", "args", "runtimeArgs" })
                {
                    if (!item.TryGetProperty(name, out var value))
                        continue;

                    if (value.ValueKind == JsonValueKind.String)
                    {
                        yield return (value.GetString() ?? string.Empty, owner);
                    }
                    else if (value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var element in value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String))
                            yield return (element.GetString() ?? string.Empty, owner);
                    }
                }
            }
        }
    }

    // A configuration or task name, or an empty string when it has none (the owner is still reported).
    private static string TextOf(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static IEnumerable<JsonElement> Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : [];

    private static IEnumerable<string> ScriptTokens(string value)
    {
        foreach (Match m in Token.Matches(value))
        {
            var token = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            if (ScriptExtensions.Contains(Path.GetExtension(token), StringComparer.OrdinalIgnoreCase))
                yield return token;
        }
    }

    // The repo-relative path of an existing file inside the repo, or null.
    private static string? Resolve(string root, string[] baseDirs, string token)
    {
        var text = token.Replace("${workspaceFolder}", root, StringComparison.Ordinal).Replace('/', Path.DirectorySeparatorChar);
        foreach (var baseDir in baseDirs)
        {
            string full;
            try
            {
                full = Path.GetFullPath(Path.IsPathRooted(text) ? text : Path.Combine(baseDir, text));
            }
            catch (ArgumentException)
            {
                continue;
            }

            var rel = Path.GetRelativePath(root, full);
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) || !File.Exists(full))
                continue;

            return rel.Replace(Path.DirectorySeparatorChar, '/');
        }

        return null;
    }
}

/// <summary>Adds script entries to a project's .elevated.json text.</summary>
public static class ManifestEditor
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string AddScript(string manifestJson, string path)
    {
        var root = JsonNode.Parse(manifestJson)?.AsObject() ?? throw new JsonException(".elevated.json is not an object");
        var scripts = root["scripts"]?.AsArray() ?? new JsonArray();
        root["scripts"] = scripts;

        if (!scripts.Any(s => string.Equals(s?["path"]?.GetValue<string>(), path, StringComparison.OrdinalIgnoreCase)))
        {
            scripts.Add(new JsonObject { ["path"] = path });
        }

        return root.ToJsonString(Indented) + "\n";
    }
}
