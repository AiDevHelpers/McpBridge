using System.Text.Json;

namespace AiDevHelpers.Mcp.Client;

/// <summary>What the HTTP MCP wrote about itself, plus the bearer token.</summary>
/// <param name="Pid">The process id of the server.</param>
/// <param name="Port">The loopback port.</param>
/// <param name="Version">The server version.</param>
/// <param name="Error">A startup error such as <c>port-in-use</c>, or null.</param>
/// <param name="Token">The bearer token, empty when the token file is missing.</param>
public sealed record McpEndpoint(int Pid, int Port, string Version, string? Error, string Token);

public enum EndpointReadKind
{
    Missing,
    Invalid,
    Found,
}

public sealed record EndpointReadResult(EndpointReadKind Kind, McpEndpoint? Endpoint = null);

/// <summary>Reads the server's endpoint record and token from its data folder (<c>%ProgramData%\AiDevHelpers\mcp-http</c>).</summary>
public static class McpEndpointFiles
{
    public static string DefaultFolder =>
        Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "AiDevHelpers", "mcp-http");

    public static EndpointReadResult Read(string folder) =>
        Read(Path.Combine(folder, "endpoint.json"), Path.Combine(folder, "token"));

    /// <summary>Reads the record and the token from explicit paths. A missing token file gives an empty token.</summary>
    public static EndpointReadResult Read(string recordPath, string tokenPath)
    {
        try
        {
            if (!File.Exists(recordPath))
            {
                return new EndpointReadResult(EndpointReadKind.Missing);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(recordPath));
            var root = document.RootElement;
            var pid = root.GetProperty("pid").GetInt32();
            var port = root.GetProperty("port").GetInt32();
            var version = root.GetProperty("version").GetString() ?? string.Empty;
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;

            var token = File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : string.Empty;
            return new EndpointReadResult(EndpointReadKind.Found, new McpEndpoint(pid, port, version, error, token));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IOException or UnauthorizedAccessException)
        {
            return new EndpointReadResult(EndpointReadKind.Invalid);
        }
    }
}
