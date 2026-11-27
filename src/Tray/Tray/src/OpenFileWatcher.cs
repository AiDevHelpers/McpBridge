using System.Reactive.Linq;
using System.Reactive.Subjects;
using AiDevHelpers.Bridge;

namespace AiDevHelpers.Tray;

/// <summary>
/// Runs one events session per running instance and reports the file each one has open. Sync is called with the instance list each
/// time it changes: a new instance gets a session, and an instance that left has its session cancelled and its file dropped.
/// </summary>
public sealed class OpenFileWatcher : IDisposable
{
    /// <summary>Runs one events session until it ends or <paramref name="ct"/> is cancelled. Production uses <see cref="InstanceEventsClient.RunAsync"/>.</summary>
    public delegate Task EventsSession(string pipeName, Action<EventLine> onEvent, CancellationToken ct);

    private static readonly IReadOnlyDictionary<int, string?> Empty = new Dictionary<int, string?>();

    private readonly EventsSession _session;
    private readonly object _gate = new();
    private readonly Dictionary<int, (CancellationTokenSource Cts, OpenFileTracker Tracker)> _running = new();
    private readonly Dictionary<int, string?> _current = new();
    private readonly BehaviorSubject<IReadOnlyDictionary<int, string?>> _snapshots = new(Empty);

    public OpenFileWatcher(EventsSession session) => _session = session;

    /// <summary>The open file per instance, replayed to a new subscriber. An instance with no event yet has null.</summary>
    public IObservable<IReadOnlyDictionary<int, string?>> Snapshots => _snapshots.AsObservable();

    /// <summary>The open file per running instance right now.</summary>
    public IReadOnlyDictionary<int, string?> Current
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<int, string?>(_current);
            }
        }
    }

    /// <summary>The events pipe of an instance: VS Code and Visual Studio use different names.</summary>
    public static string EventPipeFor(VsInstance instance) =>
        instance.Ide == "VsCode"
            ? InstanceRegistry.VsCodeEventPipeNameFor(instance.Pid)
            : InstanceRegistry.EventPipeNameFor(instance.Pid);

    /// <summary>Makes the running sessions match the instance list.</summary>
    public void Sync(IReadOnlyList<VsInstance> instances)
    {
        IReadOnlyDictionary<int, string?> snapshot;
        lock (_gate)
        {
            var live = instances.Select(i => i.Pid).ToHashSet();
            foreach (var pid in _running.Keys.Where(pid => !live.Contains(pid)).ToList())
            {
                _running[pid].Cts.Cancel();
                _running[pid].Cts.Dispose();
                _running.Remove(pid);
                _current.Remove(pid);
            }

            foreach (var instance in instances.Where(i => !_running.ContainsKey(i.Pid)))
            {
                var cts = new CancellationTokenSource();
                var tracker = new OpenFileTracker();
                _running[instance.Pid] = (cts, tracker);
                _current[instance.Pid] = null;
                _ = RunAsync(instance.Pid, EventPipeFor(instance), tracker, cts.Token);
            }

            snapshot = new Dictionary<int, string?>(_current);
        }

        _snapshots.OnNext(snapshot);
    }

    private async Task RunAsync(int pid, string pipeName, OpenFileTracker tracker, CancellationToken ct)
    {
        try
        {
            await _session(pipeName, e => Apply(pid, tracker, e), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The session failed. The instance stays listed with its last known file; a new session starts when it leaves and returns.
            System.Diagnostics.Trace.TraceWarning($"Events session for pid {pid} failed: {ex.Message}");
        }
    }

    private void Apply(int pid, OpenFileTracker tracker, EventLine e)
    {
        IReadOnlyDictionary<int, string?> snapshot;
        lock (_gate)
        {
            if (!_running.ContainsKey(pid))
            {
                return;
            }

            tracker.Apply(e);
            _current[pid] = tracker.Current;
            snapshot = new Dictionary<int, string?>(_current);
        }

        _snapshots.OnNext(snapshot);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var (cts, _) in _running.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _running.Clear();
        }

        _snapshots.OnCompleted();
        _snapshots.Dispose();
    }
}
