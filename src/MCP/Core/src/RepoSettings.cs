using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>The settings in effect for one repo, and anything in the repo file that was ignored or unreadable.</summary>
internal sealed record RepoSettingsResult(IdeToolsOptions Options, IReadOnlyList<string> Warnings, bool AllowAllRequested = false);

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
        var allowAll = false;

        var root = FindRepoRoot(solutionPath);
        var file = root is null ? null : Path.Combine(root, FolderName, FileName);
        if (file is null || !File.Exists(file))
        {
            return new RepoSettingsResult(effective, warnings);
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));

            // allowAll is repo-only and only a request: it takes effect when this user's signed approval matches (see the plan).
            allowAll = doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(AllowAllKey, out var requested)
                && requested.ValueKind == JsonValueKind.True;

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
            allowAll = false;
        }
        catch (IOException ex)
        {
            warnings.Add(Strings.Format("RepoSettingsUnreadable", file, ex.Message));
            allowAll = false;
        }

        return new RepoSettingsResult(effective, warnings, allowAll);
    }

    /// <summary>A global allowAll is never honored. Returns a warning when the global configuration sets one, else null.</summary>
    public static string? GlobalAllowAllWarning(IConfiguration globalConfiguration) =>
        globalConfiguration.GetSection(AllowAllKey).Exists() || globalConfiguration.GetSection(SectionName).GetSection(AllowAllKey).Exists()
            ? Strings.Get("GlobalAllowAllIgnored")
            : null;

    /// <summary>
    /// SHA-256 of a settings file with a leading BOM removed and line endings turned into LF, so a checkout on another
    /// platform or an editor that adds a BOM does not change the hash.
    /// </summary>
    public static string HashFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        var text = Encoding.UTF8.GetString(bytes, start, bytes.Length - start).Replace("\r\n", "\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Adds <c>.aidevtools/user.json</c> to the repo's .gitignore when it is not already listed.</summary>
    public static void EnsureUserFileGitIgnored(string repoRoot)
    {
        var path = Path.Combine(repoRoot, ".gitignore");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        if (lines.Contains(UserFileIgnoreLine))
        {
            return;
        }

        lines.Add(UserFileIgnoreLine);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
    }

    private const string AllowAllKey = "allowAll";
    private const string UserFileIgnoreLine = ".aidevtools/user.json";

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
