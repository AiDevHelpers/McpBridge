namespace AiDevHelpers.Helpers.ModelServerHelper;

/// <summary>The model_route result: the chosen model name, or an error when the key or its route is not known.</summary>
public sealed record RouteChoice(bool Ok, string? Model, string? Error);

/// <summary>
/// Maps a model type (any name the caller uses, such as code, chat or triage) to a model name from a caller-supplied route table.
/// No model types are built in: the route table defines them. The router has no endpoint selection, so a route is a model name
/// the router lists. Types are matched case-insensitively.
/// </summary>
public static class ModelRouter
{
    public static RouteChoice Resolve(string modelType, IReadOnlyDictionary<string, string> routes)
    {
        var normalized = (modelType ?? "").Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return new RouteChoice(false, null, Strings.Get("RouteTypeEmpty"));

        return routes.TryGetValue(normalized, out var model) && !string.IsNullOrWhiteSpace(model)
            ? new RouteChoice(true, model, null)
            : new RouteChoice(false, null, Strings.Format("RouteMissing", normalized));
    }
}
