using System.Text.Json.Nodes;

namespace AiDevHelpers.Host;

/// <summary>
/// Reads the shared control file that <c>server_control_set</c> writes (<c>%ProgramData%\AiDevHelpers\control\state.json</c>).
/// The supervisor holds back restarts while the state is <c>closing</c>, so an update can replace the server's files.
/// The file is writable by users; it can only delay a restart (and only up to the supervisor's limit), never start anything.
/// </summary>
public static class ControlFile
{
    /// <summary>The default control file path; the environment variable <c>AIDEVHELPERS_CONTROL_STATE</c> overrides it, as in the server.</summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("AIDEVHELPERS_CONTROL_STATE")
        ?? Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "AiDevHelpers", "control", "state.json");

    /// <summary>True when the file says <c>closing</c>. A missing or unreadable file is not closing.</summary>
    public static bool IsClosing(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            if (!File.Exists(file))
            {
                return false;
            }

            return (JsonNode.Parse(File.ReadAllText(file)) as JsonObject)?["state"]?.GetValue<string>() == "closing";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
