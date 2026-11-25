using System;
using System.Text.Json;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// Severity of each bridge event (plan section 4.2). Pure: it reads the encoded event line, so the filter needs no parsed
/// copy. Unknown names are Info.
/// </summary>
internal static class EventSeverityMap
{
    public static EventSeverity Of(string name, string json)
    {
        switch (name)
        {
            case "dropped":
                return EventSeverity.Warning;
            case "enterBreakMode":
                return BreakSeverity(ReasonOf(json));
            default:
                return EventSeverity.Info;
        }
    }

    /// <summary>Parses a severity name from a caller argument: "info", "warning" or "error", any case. Null means Info.</summary>
    public static bool TryParse(string? text, out EventSeverity severity)
    {
        if (text is null)
        {
            severity = EventSeverity.Info;
            return true;
        }

        switch (text.Trim().ToLowerInvariant())
        {
            case "info":
                severity = EventSeverity.Info;
                return true;
            case "warning":
                severity = EventSeverity.Warning;
                return true;
            case "error":
                severity = EventSeverity.Error;
                return true;
            default:
                severity = EventSeverity.Info;
                return false;
        }
    }

    private static EventSeverity BreakSeverity(string? reason) => reason switch
    {
        "dbgEventReasonExceptionNotHandled" => EventSeverity.Error,
        "dbgEventReasonExceptionThrown" => EventSeverity.Warning,
        _ => EventSeverity.Info,
    };

    private static string? ReasonOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("reason", out var reason)
                && reason.ValueKind == JsonValueKind.String)
            {
                return reason.GetString();
            }
        }
        catch (JsonException)
        {
            // A malformed line has no reason; it is Info.
        }

        return null;
    }
}
