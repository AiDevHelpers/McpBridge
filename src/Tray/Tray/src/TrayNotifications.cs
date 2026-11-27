namespace AiDevHelpers.Tray;

/// <summary>The toast text for a change of tray icon state. Nothing is shown at startup or when the state did not change.</summary>
public static class TrayNotifications
{
    public static string? For(TrayIconState? previous, TrayIconState current)
    {
        if (previous is null || previous == current)
        {
            return null;
        }

        return current switch
        {
            TrayIconState.Running => Strings.Get("ServerRunning"),
            TrayIconState.Degraded => Strings.Get("ServerNeedsAttention"),
            TrayIconState.Disconnected => Strings.Get("ServerNotReachable"),
            _ => Strings.Get("ServiceStoppedNotice"),
        };
    }
}

