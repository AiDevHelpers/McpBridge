using TokenSaver.AgentHelpers.Core.Contracts;
using TokenSaver.AgentHelpers.Core.Models;
using TokenSaver.AgentHelpers.Core.Services;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// Chooses the context format for the calling model and client (AiDevHelpers.AgentHelpers) and fits tool text to its token budget.
/// Pure: no server or request objects, so the tests run without a host.
/// </summary>
public static class AgentFormatting
{
    /// <summary>Builds the caller from whatever the transport knows: MCP client info and/or HTTP headers.</summary>
    public static CallerInfo CallerFrom(string? clientName, string? clientVersion, string? modelHint, string? userAgent) => new()
    {
        ClientName = clientName ?? string.Empty,
        ClientVersion = clientVersion,
        ModelHint = string.IsNullOrWhiteSpace(modelHint) ? null : modelHint.Trim().ToLowerInvariant(),
        UserAgent = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent,
        Source = string.IsNullOrWhiteSpace(clientName) ? DetectionSource.Default : DetectionSource.McpClientInfo,
    };

    /// <summary>The model adapter's format, with the client adapter's flags merged on top.</summary>
    public static ContextFormat FormatFor(AgentAdapterRegistry registry, CallerInfo caller)
    {
        var (model, client) = registry.ResolveBoth(caller);
        return model.GetFormat(caller).WithClientOverrides(client.GetFormat(caller));
    }

    /// <summary>Trims text to the format's token budget. Text that already fits is returned unchanged.</summary>
    public static string Fit(string text, ContextFormat format) => TokenEstimator.TrimToBudget(text, format.ContextTokenBudget);

    /// <summary>A short note for the client's server instructions: how much each tool reply may hold for this caller.</summary>
    public static string Instructions(ContextFormat format) => Strings.Format("AgentToolReplyLimit", format.ContextTokenBudget);
}

