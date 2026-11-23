using System.Text.Json;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>The settings in effect for one repo, and anything in the repo file that was ignored or unreadable.</summary>
internal sealed record RepoSettingsResult(IdeToolsOptions Options, IReadOnlyList<string> Warnings);

/// <summary>
/// Layers <c>&lt;repo&gt;/.aidevtools/settings.json</c> over the global settings: each key the repo sets wins, the rest fall
/// through. Only the keys in <see cref="OverridableKeys"/> may come from a repo, because a cloned repo is untrusted input.
/// A missing or unreadable file never changes the global values.
/// </summary>
internal static class RepoSettings
{
    public const string FolderName = ".aidevtools";
    public const string FileName = "settings.json";
    private const string SectionName = "IdeTools";

    /// <summary>The IdeTools keys a repo file may set.</summary>
    private static readonly string[] OverridableKeys = [nameof(IdeToolsOptions.IdeTestRunMode)];

    public static RepoSettingsResult Resolve(IdeToolsOptions global, string? solutionPath)
    {
        var effective = new IdeToolsOptions { IdeTestRunMode = global.IdeTestRunMode };
        var warnings = new List<string>();

        var root = FindRepoRoot(solutionPath);
        var file = root is null ? null : Path.Combine(root, FolderName, FileName);
        if (file is null || !File.Exists(file))
        {
            return new RepoSettingsResult(effective, warnings);
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(SectionName, out var section)
                && section.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in section.EnumerateObject())
                {
                    if (!OverridableKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        warnings.Add(Strings.Format("RepoSettingIgnored", property.Name, file));
                    }
                    else if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        effective.IdeTestRunMode = property.Value.GetString() ?? effective.IdeTestRunMode;
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            warnings.Add(Strings.Format("RepoSettingsUnreadable", file, ex.Message));
            effective.IdeTestRunMode = global.IdeTestRunMode;
        }
        catch (IOException ex)
        {
            warnings.Add(Strings.Format("RepoSettingsUnreadable", file, ex.Message));
        }

        return new RepoSettingsResult(effective, warnings);
    }

    /// <summary>The nearest folder at or above the solution that has a <c>.git</c> folder or file (a worktree or submodule has a file).</summary>
    public static string? FindRepoRoot(string? solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            return null;
        }

        for (var dir = Path.GetDirectoryName(Path.GetFullPath(solutionPath)); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var git = Path.Combine(dir, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return dir;
            }
        }

        return null;
    }
}
