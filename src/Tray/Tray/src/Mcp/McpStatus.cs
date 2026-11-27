namespace AiDevHelpers.Tray.Mcp;

/// <summary>Whether the tray can reach the HTTP MCP. A lost connection is a state, never an error on the stream.</summary>
public enum ConnectionState
{
    Disconnected,
    Reconnecting,
    Connected,
}

/// <summary>What the tray knows about the HTTP MCP right now.</summary>
/// <param name="State">The connection state.</param>
/// <param name="Version">The server version when connected.</param>
/// <param name="Port">The server port when known.</param>
/// <param name="Pid">The server pid when known.</param>
/// <param name="Problem">
/// Why it is not connected: <c>no-endpoint</c>, <c>endpoint-invalid</c>, a startup error from the server such as
/// <c>port-in-use</c>, <c>no-token</c>, <c>not-listening</c>, <c>listener-mismatch</c> or <c>unhealthy</c>.
/// </param>
public sealed record McpStatus(ConnectionState State, string? Version, int? Port, int? Pid, string? Problem);
