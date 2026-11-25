using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>Severity of one event, as the filter sees it. Ordered: Info &lt; Warning &lt; Error.</summary>
public enum EventSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>One event as kept in the buffer: its sequence number, name, and encoded JSON line.</summary>
internal sealed record BufferedEvent(long Seq, string Name, string Json);

/// <summary>Filter options. Null or empty means "no restriction" for that field.</summary>
internal sealed record EventFilterOptions(
    IReadOnlyCollection<string>? Names = null,
    EventSeverity MinSeverity = EventSeverity.Info,
    int Limit = 20,
    int MaxTextChars = 300);

/// <summary>
/// The events that passed, plus what the caller needs to read on: how many matching events were left out by the limit, and
/// the sequence number to pass as sinceSeq next time.
/// </summary>
internal sealed record EventFilterResult(IReadOnlyList<JsonNode> Events, int Omitted, long NextSeq);

/// <summary>
/// Pure filter over the event buffer. Order: sinceSeq, names, minimum severity, limit, then the text cap. No I/O, so it is
/// unit-tested without Visual Studio.
/// </summary>
internal static class EventFilter
{
    /// <summary>Limit is clamped to this range; a larger value is a request for everything, which the caps exist to prevent.</summary>
    public const int MinLimit = 1;

    public const int MaxLimit = 200;

    public static EventFilterResult Apply(IEnumerable<BufferedEvent> buffer, long sinceSeq, EventFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(options);

        var limit = Math.Clamp(options.Limit, MinLimit, MaxLimit);
        var maxText = Math.Max(0, options.MaxTextChars);
        var names = options.Names is { Count: > 0 } n ? new HashSet<string>(n, StringComparer.Ordinal) : null;

        var matching = new List<BufferedEvent>();
        long lastSeq = sinceSeq;
        foreach (var evt in buffer)
        {
            if (evt.Seq <= sinceSeq)
            {
                continue;
            }

            lastSeq = Math.Max(lastSeq, evt.Seq);
            if (names is not null && !names.Contains(evt.Name))
            {
                continue;
            }

            if (EventSeverityMap.Of(evt.Name, evt.Json) < options.MinSeverity)
            {
                continue;
            }

            matching.Add(evt);
        }

        var taken = matching.Take(limit).ToList();
        var omitted = matching.Count - taken.Count;
        // The next read starts after the last event returned, so the omitted ones are read next time.
        var nextSeq = taken.Count > 0 ? taken[^1].Seq : lastSeq;

        var events = taken.Select(evt => CapText(evt.Json, maxText)).ToList();
        return new EventFilterResult(events, omitted, nextSeq);
    }

    /// <summary>Parses the event line, then cuts every string longer than <paramref name="maxText"/> and marks its object.</summary>
    private static JsonNode CapText(string json, int maxText)
    {
        var node = JsonNode.Parse(json) ?? new JsonObject();
        CapNode(node, maxText);
        return node;
    }

    private static void CapNode(JsonNode? node, int maxText)
    {
        switch (node)
        {
            case JsonObject obj:
                var truncated = false;
                foreach (var prop in obj.ToList())
                {
                    if (prop.Value is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > maxText)
                    {
                        obj[prop.Key] = text.Substring(0, maxText) + "…";
                        truncated = true;
                    }
                    else
                    {
                        CapNode(prop.Value, maxText);
                    }
                }

                if (truncated)
                {
                    obj["truncated"] = true;
                }

                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    CapNode(item, maxText);
                }

                break;
        }
    }
}
