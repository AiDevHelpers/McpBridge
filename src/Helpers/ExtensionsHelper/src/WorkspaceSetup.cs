using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiDevHelpers.Helpers.ExtensionsHelper;

/// <summary>Result of a workspace setup run.</summary>
public sealed record WorkspaceSetupResult(bool Ok, bool SettingApplied, IReadOnlyList<string> Recommended, string? Error);

/// <summary>
/// Recommends the official C# extensions in .vscode/extensions.json and, when C# Dev Kit is installed,
/// writes dotnet.defaultSolution to .vscode/settings.json. Existing keys in both files are kept.
/// </summary>
public sealed class WorkspaceSetup
{
    public const string CSharpDevKit = "ms-dotnettools.csdevkit";
    public const string CSharp = "ms-dotnettools.csharp";
    public const string DefaultSolutionKey = "dotnet.defaultSolution";

    private static readonly string[] Recommended = [CSharpDevKit, CSharp];

    private readonly ICommandRunner _runner;

    public WorkspaceSetup(ICommandRunner runner)
    {
        _runner = runner;
    }

    public async Task<WorkspaceSetupResult> SetupAsync(string folder, string solutionPath, CancellationToken ct = default)
    {
        var extensionsPath = Path.Combine(folder, ".vscode", "extensions.json");
        var settingsPath = Path.Combine(folder, ".vscode", "settings.json");

        try
        {
            WriteRecommendations(extensionsPath);
        }
        catch (JsonException ex)
        {
            return new WorkspaceSetupResult(false, false, [], $"Could not parse {extensionsPath}: {ex.Message}");
        }

        var installed = await InstalledIdsAsync(ct);
        var applied = false;
        if (installed.Contains(CSharpDevKit, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                WriteSetting(settingsPath, DefaultSolutionKey, Path.GetFullPath(solutionPath));
                applied = true;
            }
            catch (JsonException ex)
            {
                return new WorkspaceSetupResult(false, false, Recommended, $"Could not parse {settingsPath}: {ex.Message}");
            }
        }

        return new WorkspaceSetupResult(true, applied, Recommended, null);
    }

    private async Task<IReadOnlyList<string>> InstalledIdsAsync(CancellationToken ct)
    {
        var result = await _runner.RunAsync("code", ["--list-extensions", "--show-versions"], ct);
        return result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('@')[0])
            .ToList();
    }

    private static void WriteRecommendations(string path)
    {
        var root = ReadOrCreate(path);
        var list = root["recommendations"] as JsonArray ?? new JsonArray();
        var existing = list.Select(n => n?.GetValue<string>() ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in Recommended)
        {
            if (existing.Add(id))
            {
                list.Add(id);
            }
        }

        root["recommendations"] = list;
        Save(path, root);
    }

    private static void WriteSetting(string path, string key, string value)
    {
        var root = ReadOrCreate(path);
        root[key] = value;
        Save(path, root);
    }

    private static JsonObject ReadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        return JsonNode.Parse(File.ReadAllText(path), new JsonNodeOptions { PropertyNameCaseInsensitive = false }) as JsonObject
            ?? throw new JsonException("Root is not an object.");
    }

    private static void Save(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
