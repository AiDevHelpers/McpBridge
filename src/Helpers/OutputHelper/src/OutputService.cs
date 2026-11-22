using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiDevHelpers.Helpers.OutputHelper;

/// <summary>Sends one call to an IDE bridge. Injected so tests don't need a running IDE.</summary>
public interface IOutputBridge
{
    Task<string> CallAsync(string action, object args, string? solutionPath, string ide, CancellationToken ct);
}

/// <summary>
/// Writes text to a named output channel in Visual Studio or VS Code. The chosen IDE for each channel is kept in
/// <c>.claude/aidevhelpers.json</c> under <c>outputChannels</c>, so the choice persists per repo (D2, D5).
/// Reading back is not offered: VS Code cannot read channels it did not create (see ide-bridge.md Limits).
/// </summary>
public sealed class OutputService(IOutputBridge bridge)
{
    private const string ChannelsKey = "outputChannels";
    private static readonly string[] Ides = ["VisualStudio", "VsCode"];

    /// <summary>The config path for a solution: <c>&lt;solution folder&gt;\.claude\aidevhelpers.json</c>.</summary>
    public static string ConfigPathFor(string solutionPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? string.Empty, ".claude", "aidevhelpers.json");

    /// <summary>The IDE stored for a channel, or null.</summary>
    public static string? ChannelIde(string solutionPath, string channel)
    {
        var path = ConfigPathFor(solutionPath);
        if (!File.Exists(path))
        {
            return null;
        }

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        return root?[ChannelsKey]?[channel]?.GetValue<string>();
    }

    /// <summary>Stores the IDE for a channel, keeping every other key in the file.</summary>
    public static void SetChannelIde(string solutionPath, string channel, string ide)
    {
        ValidateChannel(channel);
        if (!Ides.Contains(ide, StringComparer.Ordinal))
        {
            throw new ArgumentException(Strings.Get("UseIdeValues"), nameof(ide));
        }

        var path = ConfigPathFor(solutionPath);
        var root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
            : new JsonObject();
        var channels = root[ChannelsKey] as JsonObject ?? new JsonObject();
        channels[channel] = ide;
        root[ChannelsKey] = channels;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>
    /// Writes text to the channel. The IDE is the one passed in, or the one stored for the channel, or an error.
    /// Creates the channel in the IDE if it does not exist there yet.
    /// </summary>
    public async Task<string> WriteAsync(string solutionPath, string channel, string text, string? ide, CancellationToken ct = default)
    {
        ValidateChannel(channel);
        var target = ide ?? ChannelIde(solutionPath, channel);
        if (target is null)
        {
            return JsonSerializer.Serialize(new { ok = false, error = Strings.Format("NoIdeSet", channel) });
        }

        if (!Ides.Contains(target, StringComparer.Ordinal))
        {
            return JsonSerializer.Serialize(new { ok = false, error = Strings.Get("UseIdeValues") });
        }

        return await bridge.CallAsync("outputWrite", new { channel, text }, solutionPath, target, ct);
    }

    internal static void ValidateChannel(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel) || channel.Length > 100)
        {
            throw new ArgumentException(Strings.Get("ChannelNameLength"), nameof(channel));
        }
    }
}
