using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.ServiceProcess;

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
