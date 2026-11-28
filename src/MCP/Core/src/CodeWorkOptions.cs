namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>
/// Server settings for code_work, bound from the <c>CodeWork</c> section. Off by default: every action returns AUTO_DISABLED until it is turned on.
/// <c>DraftModelType</c> is the model type (from <c>ModelServer:ModelTypes</c>) that drafts each step's file content on the router.
/// </summary>
public sealed class CodeWorkOptions
{
    public bool Enabled { get; set; }

    public string DraftModelType { get; set; } = "code";
}
