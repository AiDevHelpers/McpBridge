namespace AiDevHelpers.Mcp.Client;

/// <summary>
/// The HTTP MCP's port. It is a setting, not discovered: the server listens on <c>Http:Port</c> (default 5058), the Host
/// passes that port to the server, and the tray and the Host check the same port. Set the environment variable
/// <c>AIDEVHELPERS_MCP_PORT</c> to change it for this user's session; a value that is not a valid port uses the default.
/// </summary>
public static class McpPort
{
    public const int Default = 5058;

    public const string EnvironmentVariable = "AIDEVHELPERS_MCP_PORT";

    public static int Resolve() => Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>Reads a port value. Empty, non-numeric or out-of-range values give the default.</summary>
    public static int Parse(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536
            ? port
            : Default;
}
