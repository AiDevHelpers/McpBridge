namespace AiDevHelpers.Host;

/// <summary>
/// Lets the updater stop MCP.Http and keep it down while an update applies (unit 4a). While held the supervisor kills the process,
/// does not start it, and does not count the stop as a failure. Release lets it start again at once.
/// </summary>
public sealed class McpHold
{
    private readonly object _lock = new();
    private bool _held;
    private TaskCompletionSource _changed = NewSource();

    /// <summary>True while the process must stay down.</summary>
    public bool IsHeld
    {
        get
        {
            lock (_lock)
            {
                return _held;
            }
        }
    }

    /// <summary>Holds MCP.Http down. A running process is stopped by the supervisor.</summary>
    public void Hold() => Set(true);

    /// <summary>Lets MCP.Http start again.</summary>
    public void Release() => Set(false);

    /// <summary>Completes when <see cref="IsHeld"/> is no longer <paramref name="held"/>; at once if it already differs.</summary>
    public Task WhenChangedFrom(bool held)
    {
        lock (_lock)
        {
            return _held != held ? Task.CompletedTask : _changed.Task;
        }
    }

    private void Set(bool held)
    {
        TaskCompletionSource previous;
        lock (_lock)
        {
            _held = held;
            previous = _changed;
            _changed = NewSource();
        }

        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
