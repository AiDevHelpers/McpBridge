using System.Reactive.Concurrency;
using System.Reactive.Linq;
using AiDevHelpers.Mcp.Client;

namespace AiDevHelpers.Tray.Mcp;

/// <summary>
/// Watches the HTTP MCP on its configured port and publishes its <see cref="McpStatus"/>. Every poll checks that the process
/// listening on the port is the one <c>/healthz</c> reports (so a process that squats the port is refused), then calls
/// <c>/healthz</c>. A server that stops answering is <see cref="ConnectionState.Reconnecting"/> for a few polls, then
/// <see cref="ConnectionState.Disconnected"/>. The stream replays the current status to a new subscriber, polls only while
/// someone subscribes, and never fails.
/// </summary>
public sealed class McpStatusMonitor
{
    private const int FailuresBeforeDisconnected = 3;

    private readonly int _port;
    private readonly IListenerOwner _owner;
    private readonly IHealthClient _health;
    private bool _wasConnected;
    private int _failures;

    public McpStatusMonitor(int port, IListenerOwner owner, IHealthClient health, TimeSpan interval, IScheduler scheduler)
    {
        _port = port;
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
        var owner = _owner.OwnerPid(_port);
        if (owner is null)
        {
            return Down("not-listening");
        }

        var health = await _health.CheckAsync(_port, cancellationToken).ConfigureAwait(false);
        if (!health.Ok)
        {
            _failures++;
            var state = _wasConnected && _failures < FailuresBeforeDisconnected ? ConnectionState.Reconnecting : ConnectionState.Disconnected;
            return new McpStatus(state, null, _port, owner, "unhealthy");
        }

        if (health.Pid != owner)
        {
            return Down("listener-mismatch");
        }

        _failures = 0;
        _wasConnected = true;
        return new McpStatus(ConnectionState.Connected, health.Version, _port, owner, null);
    }

    private McpStatus Down(string problem)
    {
        _wasConnected = false;
        _failures = 0;
        return new McpStatus(ConnectionState.Disconnected, null, _port, null, problem);
    }
}
