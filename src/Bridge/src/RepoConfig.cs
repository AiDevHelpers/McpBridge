using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiDevHelpers.Bridge;

/// <summary>
/// The per-repo settings file <c>.claude/aidevhelpers.json</c>, kept next to the solution.
/// Holds the preferred IDE used when several instances have the same solution open.
/// Writes keep keys this code does not know about.
/// </summary>
public static class RepoConfig
{
    private const string PreferredIdeKey = "preferredIde";

    /// <summary>The config path for a solution: <c>&lt;solution folder&gt;\.claude\aidevhelpers.json</c>.</summary>
    public static string PathFor(string solutionPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? string.Empty, ".claude", "aidevhelpers.json");

    /// <summary>The stored preferred IDE ("VisualStudio" or "VsCode"), or null when none is stored.</summary>
    public static string? PreferredIde(string solutionPath)
    {
        var path = PathFor(solutionPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return root?[PreferredIdeKey]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Stores the preferred IDE, keeping any other keys already in the file.</summary>
    public static void SetPreferredIde(string solutionPath, string ide)
    {
        if (ide is not ("VisualStudio" or "VsCode"))
        {
            throw new ArgumentException("Use VisualStudio or VsCode.", nameof(ide));
        }

        var path = PathFor(solutionPath);
        var root = ReadObject(path);
        root[PreferredIdeKey] = ide;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static JsonObject ReadObject(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        // A malformed file throws, so its contents are never overwritten silently.
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
    }
}
