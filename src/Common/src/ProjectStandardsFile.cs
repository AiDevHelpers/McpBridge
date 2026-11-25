using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AiDevHelpers.Common;

/// <summary>A framework a single project may target instead of the base framework, with the reason it is allowed.</summary>
public sealed record FrameworkException(string Project, string Framework, string Reason);

/// <summary>The repo's build standards: the base target framework, the properties and analyzers every project gets, and the exceptions.</summary>
public sealed record ProjectStandards(
    string BaseFramework,
    IReadOnlyList<string> AllowedPlatformSuffixes,
    IReadOnlyDictionary<string, string> Properties,
    IReadOnlyList<string> Analyzers,
    IReadOnlyList<FrameworkException> Exceptions);

/// <summary>
/// The standards read result. <see cref="Standards"/> is null when the file is missing or rejected as a whole; <see cref="Warnings"/>
/// says what was ignored. Reading never throws and never writes.
/// </summary>
public sealed record ProjectStandardsResult(ProjectStandards? Standards, IReadOnlyList<string> Warnings);

/// <summary>Reads <c>.aidevhelpers/project-standards.json</c> with the same leniency as the other repo settings files.</summary>
public static class ProjectStandardsFile
{
    public const string FileName = "project-standards.json";

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly Regex Framework = new(@"^(net\d+\.\d+|net\d{2,3}|netstandard\d+\.\d+|netcoreapp\d+\.\d+)(-[a-z0-9.]+)?$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "baseFramework", "allowedPlatformSuffixes", "properties", "analyzers", "exceptions",
    };

    public static string FilePath(string repoRoot) => Path.Combine(repoRoot, RepoSettingsFile.FolderName, FileName);

    /// <summary>True for a framework moniker such as <c>net10.0</c> or <c>net48</c>.</summary>
    public static bool IsFramework(string? value) => value is not null && Framework.IsMatch(value);

    /// <summary>True for a repo-relative project path with no rooted part and no <c>..</c> segment.</summary>
    public static bool IsSafeProjectPath(string? path) => ElevationFiles.IsSafePath(path);

    public static ProjectStandardsResult Read(string repoRoot)
    {
        var file = FilePath(repoRoot);
        if (!File.Exists(file))
        {
            return new ProjectStandardsResult(null, []);
        }

        var warnings = new List<string>();
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(file), documentOptions: Lenient) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new ProjectStandardsResult(null, [$"{file} is unreadable and was ignored: {ex.Message}"]);
        }

        if (root is null)
        {
            return new ProjectStandardsResult(null, [$"{file} must be a JSON object and was ignored"]);
        }

        foreach (var key in root.Select(p => p.Key).Where(k => !KnownKeys.Contains(k)))
        {
            warnings.Add($"{file}: unknown key '{key}' was ignored");
        }

        var baseFramework = root["baseFramework"]?.GetValue<string?>();
        if (!IsFramework(baseFramework))
        {
            warnings.Add($"{file}: baseFramework '{baseFramework}' is not a framework moniker; the standards were ignored");
            return new ProjectStandardsResult(null, warnings);
        }

        var suffixes = StringList(root["allowedPlatformSuffixes"], file, "allowedPlatformSuffixes", warnings)
            .Where(s => Regex.IsMatch(s, "^-[a-z]+$"))
            .ToList();

        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root["properties"] is JsonObject props)
        {
            foreach (var (name, node) in props.Select(p => (p.Key, p.Value)))
            {
                if (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
                {
                    properties[name] = text;
                }
                else
                {
                    warnings.Add($"{file}: property '{name}' must be a non-empty string and was ignored");
                }
            }
        }

        var analyzers = StringList(root["analyzers"], file, "analyzers", warnings);

        var exceptions = new List<FrameworkException>();
        if (root["exceptions"] is JsonArray array)
        {
            foreach (var item in array.OfType<JsonObject>())
            {
                var project = item["project"]?.GetValue<string?>();
                var framework = item["framework"]?.GetValue<string?>();
                var reason = item["reason"]?.GetValue<string?>();
                if (!IsSafeProjectPath(project))
                {
                    warnings.Add($"{file}: exception project '{project}' is not inside the repo and was ignored");
                }
                else if (!IsFramework(framework))
                {
                    warnings.Add($"{file}: exception for '{project}' has framework '{framework}' which is not a moniker and was ignored");
                }
                else if (string.IsNullOrWhiteSpace(reason))
                {
                    warnings.Add($"{file}: exception for '{project}' has no reason and was ignored");
                }
                else
                {
                    exceptions.Add(new FrameworkException(ElevationFiles.Normalize(project!), framework!, reason));
                }
            }
        }

        var standards = new ProjectStandards(
            baseFramework!,
            suffixes,
            properties,
            analyzers,
            exceptions);
        return new ProjectStandardsResult(standards, warnings);
    }

    private static List<string> StringList(JsonNode? node, string file, string key, List<string> warnings)
    {
        var list = new List<string>();
        if (node is null)
        {
            return list;
        }

        if (node is not JsonArray array)
        {
            warnings.Add($"{file}: {key} must be an array and was ignored");
            return list;
        }

        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
            {
                list.Add(text);
            }
            else
            {
                warnings.Add($"{file}: a {key} entry is not a non-empty string and was ignored");
            }
        }

        return list;
    }
}
