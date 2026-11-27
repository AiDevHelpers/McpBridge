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
            TrayIconState.Running => "MCP server running",
            TrayIconState.Degraded => "MCP server needs attention",
            TrayIconState.Disconnected => "MCP server not reachable",
            _ => "AiDevHelpers service stopped",
        };
    }
}

