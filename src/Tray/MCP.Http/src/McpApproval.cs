using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Http;

/// <summary>Puts the user's approval in front of dangerous tool calls over the streamable-HTTP MCP.</summary>
public static class McpApproval
{
    /// <summary>How long the user has to answer a confirmation in the tray before the call is refused.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Adds the approval filter to the tool calls. Dangerous tools need the user's approval for this exact call; anything unclear refuses it.</summary>
    public static IMcpServerBuilder WithApproval(this IMcpServerBuilder builder, IApprovalChannel channel, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(channel);
        var gate = new ApprovalGate(channel, timeout);
        return builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, ct) =>
        {
            var arguments = context.Params?.Arguments is { } args ? System.Text.Json.JsonSerializer.Serialize(args) : "{}";
            var decision = await gate.CheckAsync(context.Params?.Name, arguments, ct);
            if (!decision.Allowed)
            {
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = decision.Reason ?? "The tool call was refused." }],
                };
            }

            return await next(context, ct);
        }));
    }
}
