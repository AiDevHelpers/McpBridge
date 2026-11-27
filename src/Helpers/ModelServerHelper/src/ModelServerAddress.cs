using System.Text.Json;

namespace AiDevHelpers.Helpers.ModelServerHelper;

/// <summary>
/// Finds the SimpleModelServer router address. Default <c>http://localhost:11435</c>. Reads only
/// <c>global.routerHost</c> and <c>global.routerPort</c> from <c>%ProgramData%\SimpleModelServer\SimpleModelServer.config.json</c>,
/// and accepts only a loopback host, because that folder can be writable by other users. Nothing else in the file is read.
/// </summary>
public static class ModelServerAddress
{
    public const string ConfigFileName = "SimpleModelServer.config.json";
    private const string DefaultHost = "localhost";
    private const int DefaultPort = 11435;

    /// <summary>The default config folder, <c>%ProgramData%\SimpleModelServer</c>.</summary>
    public static string DefaultConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SimpleModelServer");

    public static Uri Resolve(string? configDirectory = null)
    {
        var host = DefaultHost;
        var port = DefaultPort;
        try
        {
            var file = Path.Combine(configDirectory ?? DefaultConfigDirectory, ConfigFileName);
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("global", out var global) && global.ValueKind == JsonValueKind.Object)
                {
                    var h = ReadString(global, "routerHost", "router_host");
                    if (h is not null && !IsLoopback(h))
                        return new UriBuilder("http", DefaultHost, DefaultPort).Uri; // untrusted host: ignore the whole file

                    if (h is not null)
                        host = h;

                    var p = ReadInt(global, "routerPort", "router_port");
                    if (p is >= 1 and <= 65535)
                        port = p.Value;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // malformed or unreadable: keep the defaults
        }

        return new UriBuilder("http", host, port).Uri;
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip));

    private static string? ReadString(JsonElement obj, params string[] names)
    {
        foreach (var n in names)
        {
            if (obj.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }

        return null;
    }

    private static int? ReadInt(JsonElement obj, params string[] names)
    {
        foreach (var n in names)
        {
            if (obj.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
                return i;
        }

        return null;
    }
}
