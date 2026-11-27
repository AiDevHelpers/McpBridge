using System.Net;
using System.Net.Http;

namespace AiDevHelpers.Tray;

internal static class Program
{
    // Entry point. The tray shell arrives in unit 5 of docs/plans/2026-11-26-tray-service.md.
    [STAThread]
    private static int Main() => 0;
}

/// <summary>What the tray shows for the MCP server: reachable, unreachable, or refused by a port conflict.</summary>
public enum TrayConnection
{
    Connected,
    Disconnected,
    PortConflict,
}

/// <summary>One reading of the MCP server's state.</summary>
public sealed record HealthSnapshot(TrayConnection State, string Detail);

/// <summary>
/// Reads the MCP server's state once: a healthy /healthz reply is Connected; a port conflict the server wrote to its status file is
/// PortConflict (no /healthz can answer then); anything else is Disconnected. Failures never throw; they are a reading.
/// </summary>
public static class HealthPoller
{
    public static async Task<HealthSnapshot> ReadOnceAsync(HttpMessageHandler handler, Uri healthz, Func<string?> readStatusFile, CancellationToken ct)
    {
        var status = readStatusFile();
        if (status is not null && status.Contains("\"state\":\"portConflict\"", StringComparison.Ordinal))
        {
            return new HealthSnapshot(TrayConnection.PortConflict, status);
        }

        try
        {
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync(healthz, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? new HealthSnapshot(TrayConnection.Connected, "healthz ok")
                : new HealthSnapshot(TrayConnection.Disconnected, $"healthz {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new HealthSnapshot(TrayConnection.Disconnected, ex.GetType().Name);
        }
    }
}
