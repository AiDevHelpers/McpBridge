using System.IO;
using System.Text.Json;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// The port the HTTP MCP listens on, and the tray reads too. It comes from <c>%ProgramData%\AiDevHelpers\mcp-http\settings.json</c>
/// (<c>{"port": 5058}</c>), and the default when that file is missing or unusable. The file is read by two processes, so one reader.
/// </summary>
public static class McpPortSettings
{
    public const int DefaultPort = 5058;

    public static string DefaultPath =>
        Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "AiDevHelpers", "mcp-http", "settings.json");

    /// <summary>The port in the file, when it is a whole number from 1024 to 65535; otherwise <see cref="DefaultPort"/>.</summary>
    public static int Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return DefaultPort;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("port", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var port)
                && port is >= 1024 and <= 65535)
            {
                return port;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unusable: the default applies.
        }

        return DefaultPort;
    }
}
