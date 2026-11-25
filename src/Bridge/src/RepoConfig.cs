using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiDevHelpers.Bridge;

/// <summary>Wait and cap limits from the "eventFilter" object, with their defaults.</summary>
public sealed record EventFilterSettings(int WaitSeconds = 120, int MaxEvents = 20, int MaxEventTextChars = 300, int MaxSummaryMessageChars = 200, int MaxReplyChars = 6000);

/// <summary>
/// The per-repo settings file <c>.claude/aidevhelpers.json</c>, kept next to the solution.
/// Holds the preferred IDE used when several instances have the same solution open.
/// Writes keep keys this code does not know about.
/// </summary>
public static class RepoConfig
{
    private const string PreferredIdeKey = "preferredIde";
    private const string EventFilterKey = "eventFilter";

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

    /// <summary>
    /// The "eventFilter" object: wait and cap limits (plan 9). Each field is optional. A value outside its range falls back to
    /// the default and is named in the warnings, so a bad setting never fails a call.
    /// </summary>
    public static (EventFilterSettings Settings, IReadOnlyList<string> Warnings) ReadEventFilter(string solutionPath)
    {
        var defaults = new EventFilterSettings();
        var path = PathFor(solutionPath);
        if (!File.Exists(path))
        {
            return (defaults, []);
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return (defaults, ["aidevhelpers.json could not be read; default limits used: " + ex.Message]);
        }

        if (root?[EventFilterKey] is not JsonObject section)
        {
            return (defaults, []);
        }

        var warnings = new List<string>();
        var settings = new EventFilterSettings(
            WaitSeconds: WholeNumber(section, "waitSeconds", defaults.WaitSeconds, 1, 3600, warnings),
            MaxEvents: WholeNumber(section, "maxEvents", defaults.MaxEvents, 1, 200, warnings),
            MaxEventTextChars: WholeNumber(section, "maxEventTextChars", defaults.MaxEventTextChars, 20, 10000, warnings),
            MaxSummaryMessageChars: WholeNumber(section, "maxSummaryMessageChars", defaults.MaxSummaryMessageChars, 20, 2000, warnings),
            MaxReplyChars: WholeNumber(section, "maxReplyChars", defaults.MaxReplyChars, 500, 50000, warnings));
        return (settings, warnings);
    }

    private static int WholeNumber(JsonObject section, string key, int fallback, int min, int max, List<string> warnings)
    {
        if (!section.TryGetPropertyValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue value && value.TryGetValue<int>(out var number) && number >= min && number <= max)
        {
            return number;
        }

        warnings.Add($"eventFilter.{key} must be a whole number from {min} to {max}; default {fallback} used.");
        return fallback;
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
