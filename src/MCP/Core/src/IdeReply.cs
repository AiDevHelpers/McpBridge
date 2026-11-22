using System.Text.Json;

namespace AiDevHelpers.MCP.Core.Services.LogSources;

/// <summary>Unwraps a bridge reply. A reply with ok=false becomes an IOException carrying the bridge's message.</summary>
internal static class IdeReply
{
    public static JsonElement ResultOrThrow(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("ok", out JsonElement ok) && ok.GetBoolean())
            {
                return root.TryGetProperty("result", out JsonElement result) ? result.Clone() : default;
            }

            string error = root.TryGetProperty("error", out JsonElement e) ? e.GetString() ?? Strings.Get("UnknownError") : Strings.Get("UnknownError");
            throw new IOException(error);
        }
        catch (JsonException ex)
        {
            throw new IOException(Strings.Format("MalformedBridgeReply", ex.Message), ex);
        }
    }
}
