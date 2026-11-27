namespace AiDevHelpers.MCP.Http;

/// <summary>Counts the MCP sessions that are running now. /healthz reports the count, so the tray can show it.</summary>
public sealed class SessionCounter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Enter() => Interlocked.Increment(ref _count);

    public void Exit() => Interlocked.Decrement(ref _count);
}

