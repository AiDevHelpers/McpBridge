using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// Caps a test summary (plan 3.3): failing names and failure details to 20 items, each message to 200 characters. Counts are
/// kept whole. Pure, so it is tested without VS.
/// </summary>
internal static class TestSummaryCapper
{
    public const int MaxItems = 20;
    public const int MaxMessageChars = 200;

    public static JsonNode Cap(JsonElement summary, int maxItems = MaxItems, int maxMessageChars = MaxMessageChars)
    {
        var node = JsonNode.Parse(summary.GetRawText()) as JsonObject ?? new JsonObject();
        foreach (var prop in node.ToList())
        {
            if (prop.Value is JsonArray array && array.Count > maxItems)
            {
                var kept = array.Take(maxItems).Select(item => item?.DeepClone()).ToList();
                node[prop.Key] = new JsonArray(kept.ToArray());
                node[prop.Key + "_omitted"] = array.Count - maxItems;
            }
        }

        CapMessages(node, maxMessageChars);
        return node;
    }

    private static void CapMessages(JsonNode? node, int max)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var prop in obj.ToList())
                {
                    if (prop.Value is JsonValue value && value.TryGetValue<string>(out var text) && prop.Key == "message" && text.Length > max)
                    {
                        obj[prop.Key] = text.Substring(0, max) + "…";
                    }
                    else
                    {
                        CapMessages(prop.Value, max);
                    }
                }

                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    CapMessages(item, max);
                }

                break;
        }
    }
}
