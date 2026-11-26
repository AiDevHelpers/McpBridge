using AiDevHelpers.Helpers.LocalizationHelper;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

// MCP9005: sampling is deprecated by SEP-2577 (spec 2026-07-28) but still works, and the SDK offers no replacement that
// gets a model completion without an API key on the server. Clients that do not advertise it are refused, not called.
#pragma warning disable MCP9005
/// <summary>Completes translation prompts through the connected client's model (MCP sampling), so the server holds no API key.</summary>
internal sealed class SamplingTranslator(McpServer server) : ITranslator
{
    private const int MaxTokens = 4096;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var result = await server.SampleAsync(
            new CreateMessageRequestParams
            {
                SystemPrompt = systemPrompt,
                MaxTokens = MaxTokens,
                Messages = [new SamplingMessage { Role = Role.User, Content = [new TextContentBlock { Text = userPrompt }] }],
            },
            ct);
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }

    /// <summary>True when the client said it can answer sampling requests.</summary>
    internal static bool IsAvailable(ClientCapabilities? capabilities) => capabilities?.Sampling is not null;
}
#pragma warning restore MCP9005
