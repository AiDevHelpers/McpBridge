namespace AiDevHelpers.Helpers.TestingHelper;

/// <summary>Where the hot-reload state came from.</summary>
public enum HotReloadSource
{
    Ide,
    DotnetWatch,
    None,
}

/// <summary>Whether hot reload is active, and which setting decided it.</summary>
public sealed record HotReloadState(HotReloadSource Source, bool Active);

/// <summary>
/// Decides hot-reload state (D16): the IDE setting wins when it is on. dotnet watch is used only when the IDE
/// setting is off or unknown. ExtensionsHelper's VS Code bridge reports ideEnabled; no file watcher is run here (D7).
/// </summary>
public static class HotReloadPolicy
{
    public static HotReloadState Resolve(bool? ideEnabled, bool dotnetWatchRunning)
    {
        if (ideEnabled == true)
        {
            return new HotReloadState(HotReloadSource.Ide, true);
        }

        return dotnetWatchRunning
            ? new HotReloadState(HotReloadSource.DotnetWatch, true)
            : new HotReloadState(HotReloadSource.None, false);
    }
}
