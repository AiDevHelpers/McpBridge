using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// The shared control state for every aidevtools server process on this machine: <c>running</c>, <c>paused</c> or <c>closing</c>.
/// Each chat that uses aidevtools has its own server process, so the state lives in a file under ProgramData that all of
/// them read. An update goes: pause (tools refuse calls), close (servers exit and release their files), update the files,
/// then resume. A chat reconnects to a fresh server on its own, and the resume clears the pause.
/// </summary>
public static class ServerControl
{
    public const string Running = "running";
    public const string Paused = "paused";
    public const string Closing = "closing";

    private static readonly string[] States = [Running, Paused, Closing];

    /// <summary>
    /// The control file: <c>%ProgramData%\AiDevHelpers\control\state.json</c>. The environment variable
    /// <c>AIDEVHELPERS_CONTROL_STATE</c> names a different file, which the tests use so they never touch ProgramData.
    /// </summary>
    public static string StatePath =>
        Environment.GetEnvironmentVariable("AIDEVHELPERS_CONTROL_STATE")
        ?? Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "AiDevHelpers", "control", "state.json");

    /// <summary>The current state. A missing or unreadable file means running.</summary>
    public static ControlState Read()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return new ControlState(Running, null, DateTime.MinValue);
            }

            var node = JsonNode.Parse(File.ReadAllText(StatePath)) as JsonObject;
            var state = node?["state"]?.GetValue<string>();
            if (state is null || !States.Contains(state))
            {
                return new ControlState(Running, null, DateTime.MinValue);
            }

            var message = node?["message"]?.GetValue<string>();
            var updated = node?["updatedUtc"]?.GetValue<DateTime>() ?? DateTime.MinValue;
            return new ControlState(state, message, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return new ControlState(Running, null, DateTime.MinValue);
        }
    }

    /// <summary>Writes the state for every server process to see.</summary>
    public static ControlState Write(string state, string? message)
    {
        if (!States.Contains(state))
        {
            throw new ArgumentException(Strings.Format("UnknownControlState", string.Join(", ", States)), nameof(state));
        }

        var written = new ControlState(state, message, DateTime.UtcNow);
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var json = new JsonObject
        {
            ["state"] = written.State,
            ["message"] = written.Message,
            ["updatedUtc"] = written.UpdatedUtc,
        };
        // Write to a temp file and rename so a reader never sees half a file.
        var temp = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(temp, StatePath, overwrite: true);
        return written;
    }
}

/// <summary>State is running, paused or closing. Message is shown to the client while it is not running.</summary>
public sealed record ControlState(string State, string? Message, DateTime UpdatedUtc)
{
    public bool IsRunning => State == ServerControl.Running;
}
