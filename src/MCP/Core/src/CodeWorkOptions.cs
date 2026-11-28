namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>
/// Server settings for code_work, bound from the <c>CodeWork</c> section. Off by default: every action returns AUTO_DISABLED until it is turned on.
/// <c>DraftModelType</c> is the model type (from <c>ModelServer:ModelTypes</c>) that drafts ordinary steps.
/// <c>DifficultModelType</c> is the model type that drafts steps marked <c>difficult</c> in the plan; it can be a router type or a chat type (hand-off).
/// </summary>
public sealed class CodeWorkOptions
{
    public bool Enabled { get; set; }

    public string DraftModelType { get; set; } = "code";

    public string DifficultModelType { get; set; } = "hard";
}
