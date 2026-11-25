using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// Total size guard on a summary reply (plan 3.3 and 4.6). Items are cut from the longest list first, one half at a time, until
/// the reply fits; each cut list gets an <c>_omitted</c> count, and the reply is marked <c>reply_truncated</c>. Counts and the
/// status are never cut. Pure, so it is tested without VS.
/// </summary>
internal static class ReplySizeGuard
{
    public const int MinChars = 500;
    public const int MaxChars = 50000;

    public static JsonNode Fit(JsonNode summary, int maxChars)
    {
        if (summary is not JsonObject root)
        {
            return summary;
        }

        // The marker is set before the first cut, so its length counts against the limit while cutting.
        if (root.ToJsonString().Length > maxChars)
        {
            root["reply_truncated"] = true;
        }

        while (root.ToJsonString().Length > maxChars)
        {
            var longest = root.Where(p => p.Value is JsonArray a && a.Count > 0)
                .OrderByDescending(p => ((JsonArray)p.Value!).Count)
                .FirstOrDefault();
            if (longest.Value is not JsonArray array)
            {
                break;
            }

            var keep = array.Count / 2;
            var omitted = array.Count - keep;
            var kept = array.Take(keep).Select(item => item?.DeepClone()).ToArray();
            root[longest.Key] = new JsonArray(kept);
            root[longest.Key + "_omitted"] = (root[longest.Key + "_omitted"]?.GetValue<int>() ?? 0) + omitted;
        }

        return root;
    }
}
