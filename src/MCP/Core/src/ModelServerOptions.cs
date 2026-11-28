using AiDevHelpers.Helpers.ModelServerHelper;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>One model type's setting in config: <c>Target</c> is <c>router</c> or <c>chat</c>, <c>Model</c> the model name.</summary>
public sealed class ModelTypeSetting
{
    public string Target { get; set; } = ModelRouter.RouterTarget;

    public string Model { get; set; } = "";
}

/// <summary>Server settings for the model tools, bound from the <c>ModelServer</c> section. Model types are defined here, not in code.</summary>
public sealed class ModelServerOptions
{
    /// <summary>Model types by name, for example <c>code</c> routed to a router model, or <c>hard</c> handed back to chat.</summary>
    public Dictionary<string, ModelTypeSetting> ModelTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, ModelTypeRoute> ToRoutes() =>
        ModelTypes.ToDictionary(
            kv => kv.Key,
            kv => new ModelTypeRoute(kv.Value.Target, kv.Value.Model),
            StringComparer.OrdinalIgnoreCase);
}
