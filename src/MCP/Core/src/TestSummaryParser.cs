using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// Reads the run summary that Visual Studio prints to the Tests output pane after a Test Explorer run,
/// e.g. "Test run finished: 9149 Tests (9144 Passed, 3 Failed, 2 Skipped) run in 2.6 min".
/// Counts only: the pane doesn't name the failing tests at the default log level.
/// </summary>
internal static class TestSummaryParser
{
    private static readonly Regex SummaryLine = new(
        @"Test run finished: (\d+) Tests \((\d+) Passed, (\d+) Failed, (\d+) Skipped\)",
        RegexOptions.CultureInvariant);

    /// <summary>Takes the raw searchOutput response and returns the newest summary as JSON.</summary>
    public static string ParseLatest(string searchOutputJson)
    {
        string? line = null;
        try
        {
            using var doc = JsonDocument.Parse(searchOutputJson);
            if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                return JsonSerializer.Serialize(new { ok = false, error = Strings.Get("CouldNotReadTestsPane") });
            }

            if (doc.RootElement.TryGetProperty("result", out var result)
                && result.TryGetProperty("lines", out var lines)
                && lines.ValueKind == JsonValueKind.Array)
            {
                line = lines.EnumerateArray()
                    .Select(l => l.GetString())
                    .LastOrDefault(l => l is not null && SummaryLine.IsMatch(l));
            }
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { ok = false, error = Strings.Get("UnreadableIdeOutput") });
        }

        if (line is null)
        {
            return JsonSerializer.Serialize(new { ok = false, error = Strings.Get("NoFinishedTestRunInPane") });
        }

        var m = SummaryLine.Match(line);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            source = "output_pane",
            total = int.Parse(m.Groups[1].Value),
            passed = int.Parse(m.Groups[2].Value),
            failed = int.Parse(m.Groups[3].Value),
            skipped = int.Parse(m.Groups[4].Value),
            line = line.Trim('=', ' '),
        });
    }
}
