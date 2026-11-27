using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.ServiceProcess;

namespace AiDevHelpers.Tray;

internal static class Program
{
    // Entry point: one tray per user, then the WPF shell (unit 5 of docs/plans/2026-11-26-tray-service.md).
    [STAThread]
    private static int Main() => TrayEntry.Run();
}

/// <summary>What the tray shows for the MCP server: reachable, unreachable, or refused by a port conflict.</summary>
public enum TrayConnection
{
    Connected,
    Disconnected,
    PortConflict,
}

/// <summary>One reading of the MCP server's state.</summary>
public sealed record HealthSnapshot(TrayConnection State, string Detail, int? Sessions = null);

/// <summary>
/// Reads the MCP server's state once: a healthy /healthz reply is Connected; a port conflict the server wrote to its status file is
/// PortConflict (no /healthz can answer then); anything else is Disconnected. Failures never throw; they are a reading.
/// </summary>
public static class HealthPoller
{
    /// <summary>The session count from a /healthz body, or null when the body has none or is not JSON.</summary>
    public static int? SessionsIn(string body)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("sessions", out var sessions) && sessions.TryGetInt32(out var count)
                ? count
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

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
            if (!response.IsSuccessStatusCode)
            {
                return new HealthSnapshot(TrayConnection.Disconnected, $"healthz {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new HealthSnapshot(TrayConnection.Connected, "healthz ok", SessionsIn(body));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new HealthSnapshot(TrayConnection.Disconnected, ex.GetType().Name);
        }
    }
}

/// <summary>
/// The MCP server's state as a stream: read every interval, emitted only when it changes, and the latest value replayed to a new
/// subscriber. A subscriber that stops ends the polling.
/// </summary>
public static class HealthFeed
{
    public static IObservable<HealthSnapshot> Watch(HttpMessageHandler handler, Uri healthz, Func<string?> readStatusFile, TimeSpan interval) =>
        Observable.Interval(interval)
            .SelectMany(_ => Observable.FromAsync(ct => HealthPoller.ReadOnceAsync(handler, healthz, readStatusFile, ct)))
            .DistinctUntilChanged(snapshot => snapshot.State)
            .Replay(1)
            .RefCount();
}

/// <summary>The Windows service's state, as the tray shows it.</summary>
public enum ServiceState
{
    Running,
    Stopped,
    Transitioning,
    NotInstalled,
}

/// <summary>Reads the AiDevHelpers service's state from the Service Control Manager. A standard user can read it; changing it needs administrator rights.</summary>
public static class ServiceStatus
{
    public static ServiceState Map(ServiceControllerStatus? status) => status switch
    {
        null => ServiceState.NotInstalled,
        ServiceControllerStatus.Running => ServiceState.Running,
        ServiceControllerStatus.Stopped => ServiceState.Stopped,
        _ => ServiceState.Transitioning,
    };

    public static ServiceState Read(string serviceName)
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            return Map(controller.Status);
        }
        catch (InvalidOperationException)
        {
            return ServiceState.NotInstalled;
        }
    }
}
