using System.IO;
using System.Text.Json;

namespace AiDevHelpers.Tray;

/// <summary>What the tray does at start: show the window, start it minimized on the taskbar, or start hidden in the tray.</summary>
public enum StartupMode
{
    /// <summary>The window opens at start.</summary>
    Open,

    /// <summary>The window starts minimized to the taskbar.</summary>
    Minimized,

    /// <summary>The window starts hidden in the tray, and minimizing it hides it there too.</summary>
    Tray,
}

/// <summary>The tray's own settings, in <c>%LOCALAPPDATA%\AiDevHelpers\tray\settings.json</c>: <c>{"startup":"open"}</c>.</summary>
public sealed record TraySettings(StartupMode Startup)
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiDevHelpers", "tray", "settings.json");

    /// <summary>The saved settings, or the defaults when the file is missing or unusable.</summary>
    public static TraySettings Read(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("startup", out var value)
                    && value.ValueKind == JsonValueKind.String
                    && Parse(value.GetString()) is { } mode)
                {
                    return new TraySettings(mode);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unusable: the defaults apply.
        }

        return new TraySettings(StartupMode.Open);
    }

    public static void Write(string path, StartupMode mode)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { startup = Name(mode) }));
    }

    private static string Name(StartupMode mode) => mode switch
    {
        StartupMode.Minimized => "minimized",
        StartupMode.Tray => "tray",
        _ => "open",
    };

    private static StartupMode? Parse(string? name) => name switch
    {
        "open" => StartupMode.Open,
        "minimized" => StartupMode.Minimized,
        "tray" => StartupMode.Tray,
        _ => null,
    };
}
