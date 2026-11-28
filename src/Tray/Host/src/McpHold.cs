using System.Diagnostics;

namespace AiDevHelpers.Host;

/// <summary>
/// Lets the updater stop MCP.Http and keep it down while an update applies (unit 4a). While held the supervisor kills the process,
/// does not start it, and does not count the stop as a failure. Release lets it start again at once.
/// </summary>
public sealed class McpHold
{
    private readonly object _lock = new();
    private bool _held;
    private Process? _current;
    private TaskCompletionSource _changed = NewSource();

    /// <summary>Records the MCP.Http process the supervisor just started, so <see cref="WaitUntilStoppedAsync"/> can watch it.</summary>
    public void Attach(Process process)
    {
        lock (_lock)
        {
            _current = process;
        }
    }

    /// <summary>
    /// Completes true when no MCP.Http process is running, or false when <paramref name="timeout"/> passes first. Cancelling
    /// <paramref name="ct"/> throws.
    /// </summary>
    public async Task<bool> WaitUntilStoppedAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timer = new CancellationTokenSource(timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        while (true)
        {
            Process? process;
            lock (_lock)
            {
                process = _current;
            }

            if (process is null || process.HasExited)
            {
                return true;
            }

            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timer.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return false;
            }
        }
    }

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
