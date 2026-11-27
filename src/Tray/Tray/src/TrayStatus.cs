namespace AiDevHelpers.Tray;

/// <summary>The tray icon's state, from the service and the MCP server together.</summary>
public enum TrayIconState
{
    Running,
    Degraded,
    Disconnected,
    Stopped,
}

/// <summary>Maps the service state and the MCP server's reading to the tray icon.</summary>
public static class TrayStatus
{
    public static TrayIconState IconFor(HealthSnapshot health, ServiceState service)
    {
        if (health.State == TrayConnection.Connected)
        {
            return service == ServiceState.Running ? TrayIconState.Running : TrayIconState.Degraded;
        }

        if (service is ServiceState.Stopped or ServiceState.NotInstalled)
        {
            return TrayIconState.Stopped;
        }

        return health.State == TrayConnection.PortConflict ? TrayIconState.Degraded : TrayIconState.Disconnected;
    }
}

