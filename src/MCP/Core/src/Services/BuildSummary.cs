using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// Builds the summary a build call returns (plan 4.6): status and errors only, capped. Pure, so it is tested without VS.
/// </summary>
internal static class BuildSummary
{
    public const int MaxErrors = 20;
    public const int MaxMessageChars = 200;

    /// <summary>
    /// <paramref name="buildResult"/> is the bridge's build result ({ succeeded, failed_projects }). <paramref name="errors"/> is the
    /// error list from ide_get_build_diagnostics (items with severity, file, line, column, message); null when not fetched.
    /// </summary>
    public static object Build(JsonElement buildResult, JsonElement? errors, int maxErrors = MaxErrors, int maxMessageChars = MaxMessageChars)
    {
        var succeeded = buildResult.TryGetProperty("succeeded", out var s) && s.ValueKind == JsonValueKind.True;
        var failedProjects = buildResult.TryGetProperty("failed_projects", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : 0;

        var list = new List<object>();
        var total = 0;
        if (errors is { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (!IsError(item))
                {
                    continue;
                }

                total++;
                if (list.Count < maxErrors)
                {
                    list.Add(new
                    {
                        file = Text(item, "file", 260),
                        line = Int(item, "line"),
                        column = Int(item, "column"),
                        message = Text(item, "message", maxMessageChars),
                    });
                }
            }
        }

        return new
        {
            status = "finished",
            succeeded,
            failed_projects = failedProjects,
            error_count = total,
            errors = list,
            errors_omitted = total - list.Count,
        };
    }

    private static bool IsError(JsonElement item) =>
        item.TryGetProperty("severity", out var sev) && sev.ValueKind == JsonValueKind.String
        && string.Equals(sev.GetString(), "error", StringComparison.OrdinalIgnoreCase);

    private static int Int(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static string Text(JsonElement item, string name, int max)
    {
        var text = item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        return text.Length <= max ? text : text.Substring(0, max) + "…";
    }
}
