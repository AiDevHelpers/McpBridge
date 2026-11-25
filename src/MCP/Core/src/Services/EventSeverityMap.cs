using System;
using System.Text.Json;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// Severity of each bridge event. Unit 1 stub: every event is Info. Unit 2 replaces this with the table in the plan (section 4.2).
/// </summary>
internal static class EventSeverityMap
{
    public static EventSeverity Of(string name, string json)
    {
        _ = name;
        _ = json;
        return EventSeverity.Info;
    }
}
