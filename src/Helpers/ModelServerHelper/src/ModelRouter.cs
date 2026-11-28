namespace AiDevHelpers.Helpers.ModelServerHelper;

/// <summary>
/// One model type's setting. <c>Target</c> is <c>router</c> (run on the SimpleModelServer router with <c>Model</c>) or
/// <c>chat</c> (hand back to the calling chat, which is asked to use <c>Model</c>).
/// </summary>
public sealed record ModelTypeRoute(string Target, string Model);

/// <summary>The model_route result: the target and model for a model type, or an error when it cannot be resolved.</summary>
public sealed record RouteChoice(bool Ok, string? Target, string? Model, string? Error);

/// <summary>
/// Resolves a model type to its configured target and model. No model types are built in: the server settings define them,
/// and an unknown or incomplete type is refused. Types are matched case-insensitively.
/// </summary>
public static class ModelRouter
{
    public const string RouterTarget = "router";
    public const string ChatTarget = "chat";

    public static RouteChoice Resolve(string modelType, IReadOnlyDictionary<string, ModelTypeRoute> types)
    {
        var normalized = (modelType ?? "").Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return Refused(Strings.Get("RouteTypeEmpty"));

        if (!types.TryGetValue(normalized, out var route))
            return Refused(Strings.Format("RouteMissing", normalized));

        var target = (route.Target ?? "").Trim().ToLowerInvariant();
        if (target is not (RouterTarget or ChatTarget))
            return Refused(Strings.Format("RouteTargetInvalid", normalized, route.Target ?? ""));

        if (string.IsNullOrWhiteSpace(route.Model))
            return Refused(Strings.Format("RouteModelEmpty", normalized));

        return new RouteChoice(true, target, route.Model.Trim(), null);
    }

    private static RouteChoice Refused(string error) => new(false, null, null, error);
}
