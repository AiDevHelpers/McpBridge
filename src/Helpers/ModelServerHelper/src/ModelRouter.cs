namespace AiDevHelpers.Helpers.ModelServerHelper;

/// <summary>The model_route result: the chosen model name, or an error when the key or its route is not known.</summary>
public sealed record RouteChoice(bool Ok, string? Model, string? Error);

/// <summary>
/// Maps a task key (code, chat, summarize, embed, rerank) to a model name from a caller-supplied route table.
/// The router has no endpoint selection, so a route is a model name the router lists. Keys are matched case-insensitively.
/// </summary>
public static class ModelRouter
{
    public static readonly IReadOnlyList<string> Keys = ["code", "chat", "summarize", "embed", "rerank"];

    public static RouteChoice Resolve(string key, IReadOnlyDictionary<string, string> routes)
    {
        var normalized = key.Trim().ToLowerInvariant();
        if (!Keys.Contains(normalized))
            return new RouteChoice(false, null, Strings.Format("RouteKeyUnknown", key, string.Join(", ", Keys)));

        return routes.TryGetValue(normalized, out var model) && !string.IsNullOrWhiteSpace(model)
            ? new RouteChoice(true, model, null)
            : new RouteChoice(false, null, Strings.Format("RouteMissing", normalized));
    }
}
