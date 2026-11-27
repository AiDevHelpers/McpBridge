using System.Reactive.Concurrency;
using System.Reactive.Linq;
using AiDevHelpers.Mcp.Client;

namespace AiDevHelpers.Tray.Mcp;

/// <summary>
/// Watches the HTTP MCP and publishes its <see cref="McpStatus"/>. Every poll reads the endpoint record, checks that the
/// process listening on the port is the one that wrote the record (so the token is never sent to a process that squats
/// the port), then calls <c>/healthz</c>. A server that stops answering is <see cref="ConnectionState.Reconnecting"/> for
/// a few polls, then <see cref="ConnectionState.Disconnected"/>. The stream replays the current status to a new
/// subscriber, polls only while someone subscribes, and never fails.
/// </summary>
public sealed class McpStatusMonitor
{
    private const int FailuresBeforeDisconnected = 3;

    private readonly Func<EndpointReadResult> _readEndpoint;
    private readonly IListenerOwner _owner;
    private readonly IHealthClient _health;
    private bool _wasConnected;
    private int _failures;

    public McpStatusMonitor(Func<EndpointReadResult> readEndpoint, IListenerOwner owner, IHealthClient health, TimeSpan interval, IScheduler scheduler)
    {
        _readEndpoint = readEndpoint;
        _owner = owner;
        _health = health;
        Status = Observable.Timer(TimeSpan.Zero, interval, scheduler)
            .Select(_ => Observable.FromAsync(PollAsync))
            .Concat()
            .DistinctUntilChanged()
            .Replay(1)
            .RefCount();
    }

    /// <summary>The current status, then every change.</summary>
    public IObservable<McpStatus> Status { get; }

    private async Task<McpStatus> PollAsync(CancellationToken cancellationToken)
    {
        var read = _readEndpoint();
        if (read.Kind == EndpointReadKind.Missing)
        {
            return Down("no-endpoint");
        }

        if (read.Kind == EndpointReadKind.Invalid || read.Endpoint is null)
        {
            return Down("endpoint-invalid");
        }

        var endpoint = read.Endpoint;
        if (endpoint.Error is not null)
        {
            return Down(endpoint.Error, endpoint.Port);
        }

        if (endpoint.Token.Length == 0)
        {
            return Down("no-token", endpoint.Port);
        }

        var owner = _owner.OwnerPid(endpoint.Port);
        if (owner is null)
        {
            return Down("not-listening", endpoint.Port);
        }

        if (owner != endpoint.Pid)
        {
            return Down("listener-mismatch", endpoint.Port);
        }

        var health = await _health.CheckAsync(endpoint.Port, endpoint.Token, cancellationToken).ConfigureAwait(false);
        if (!health.Ok)
        {
            _failures++;
            var state = _wasConnected && _failures < FailuresBeforeDisconnected ? ConnectionState.Reconnecting : ConnectionState.Disconnected;
            return new McpStatus(state, null, endpoint.Port, endpoint.Pid, "unhealthy");
        }

        if (health.Pid != endpoint.Pid)
        {
            return Down("listener-mismatch", endpoint.Port);
        }

        _failures = 0;
        _wasConnected = true;
        return new McpStatus(ConnectionState.Connected, health.Version, endpoint.Port, endpoint.Pid, null);
    }

    private McpStatus Down(string problem, int? port = null)
    {
        _wasConnected = false;
        _failures = 0;
        return new McpStatus(ConnectionState.Disconnected, null, port, null, problem);
    }
}
