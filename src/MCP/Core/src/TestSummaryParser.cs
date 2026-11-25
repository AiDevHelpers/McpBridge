using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// Reads the run summary that Visual Studio prints to the Tests output pane after a Test Explorer run,
/// e.g. "Test run finished: 9149 Tests (9144 Passed, 3 Failed, 2 Skipped) run in 2.6 min".
/// Failing test names come from the "[FAIL]" lines the runner prints between two summary lines; when the pane has none, the list is empty.
/// </summary>
internal static class TestSummaryParser
{
    private static readonly Regex SummaryLine = new(
        @"Test run finished: (\d+) Tests \((\d+) Passed, (\d+) Failed, (\d+) Skipped\)",
        RegexOptions.CultureInvariant);

    private static readonly Regex FailLine = new(
        @"^\s*(?:\[xUnit\.net [\d:.]+\]\s+)?(?<name>\S.*?) \[FAIL\]\s*$",
        RegexOptions.CultureInvariant);

    /// <summary>Takes the raw searchOutput response and returns the newest summary as JSON.</summary>
    public static string ParseLatest(string searchOutputJson)
    {
        string? line = null;
        var running = false;
        var failures = new List<string>();
        var details = new List<object>();
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
                var all = lines.EnumerateArray().Select(l => l.GetString() ?? string.Empty).ToList();
                var last = all.FindLastIndex(l => SummaryLine.IsMatch(l));
                running = last < 0 && all.Any(l => l.Contains("Starting:", StringComparison.Ordinal) || l.Contains("VSTest Adapter", StringComparison.Ordinal));
                if (last >= 0)
                {
                    line = all[last];
                    // The failures of the newest run are the [FAIL] lines after the previous run's summary line.
                    var previous = last > 0 ? all.FindLastIndex(last - 1, l => SummaryLine.IsMatch(l)) : -1;
                    var start = previous + 1;
                    for (var i = start; i < last; i++)
                    {
                        var fail = FailLine.Match(all[i]);
                        if (fail.Success && !failures.Contains(fail.Groups["name"].Value))
                        {
                            failures.Add(fail.Groups["name"].Value);
                            details.Add(new { name = fail.Groups["name"].Value, message = MessageAfter(all, i + 1, last) });
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { ok = false, error = Strings.Get("UnreadableIdeOutput") });
        }

        if (line is null)
        {
            // A run that has started (adapter or "Starting:" lines) but has no summary yet is still running, not missing.
            return running
                ? JsonSerializer.Serialize(new { ok = false, running = true, error = Strings.Get("TestRunStillRunning") })
                : JsonSerializer.Serialize(new { ok = false, running = false, error = Strings.Get("NoFinishedTestRunInPane") });
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
            failures,
            failure_details = details,
            line = line.Trim('=', ' '),
        });
    }

    private const int MaxMessageLines = 8;

    /// <summary>The runner lines after a [FAIL] line, up to the next [FAIL] line or the summary, without the "[xUnit.net time]" prefix.</summary>
    private static string MessageAfter(List<string> lines, int from, int end)
    {
        var message = new List<string>();
        for (var i = from; i < end && message.Count < MaxMessageLines; i++)
        {
            if (FailLine.IsMatch(lines[i]))
            {
                break;
            }

            var text = XunitPrefix.Replace(lines[i], string.Empty).Trim();
            if (text.Length > 0)
            {
                message.Add(text);
            }
        }

        return string.Join('\n', message);
    }

    private static readonly Regex XunitPrefix = new(@"^\s*\[xUnit\.net [\d:.]+\]\s*", RegexOptions.CultureInvariant);
}
