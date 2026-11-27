using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AiDevHelpers.Tray;

/// <summary>One event line from an instance's events pipe.</summary>
public sealed record EventLine(string Name, long Seq, JsonElement Data);

/// <summary>How one connection to an instance's events pipe ended.</summary>
public enum SessionEnd
{
    /// <summary>The instance said "closed" (its bridge stopped, or the subscriber was dropped for being slow).</summary>
    Closed,

    /// <summary>No line arrived inside the silence limit.</summary>
    Silent,

    /// <summary>The pipe was not there or could not be connected, or the subscribe was refused.</summary>
    Refused,
}

/// <summary>Parses the events pipe's lines. Pure, so the protocol is tested without a pipe.</summary>
public static class InstanceEvents
{
    /// <summary>The events the page uses: the open file, from Visual Studio's <c>file</c> and VS Code's <c>path</c>.</summary>
    public static readonly string[] Subscribed = ["documentOpened", "documentClosing"];

    /// <summary>The event, or null for a malformed line. Heartbeats and control lines are returned with their name.</summary>
    public static EventLine? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement.Clone();
            if (!root.TryGetProperty("event", out var name) || name.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var seq = root.TryGetProperty("seq", out var s) && s.TryGetInt64(out var value) ? value : 0;
            var data = root.TryGetProperty("data", out var d) ? d : default;
            return new EventLine(name.GetString()!, seq, data);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The file a document event names, or null when the event carries none.</summary>
    public static string? FileOf(EventLine e)
    {
        if (e.Data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (e.Data.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.String)
        {
            return file.GetString();
        }

        return e.Data.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null;
    }
}

/// <summary>The file an instance has open last: set by documentOpened, cleared when that same file is closed.</summary>
public sealed class OpenFileTracker
{
    public string? Current { get; private set; }

    public void Apply(EventLine e)
    {
        var file = InstanceEvents.FileOf(e);
        if (file is null)
        {
            return;
        }

        switch (e.Name)
        {
            case "documentOpened":
                Current = file;
                break;
            case "documentClosing" when string.Equals(Current, file, StringComparison.OrdinalIgnoreCase):
                Current = null;
                break;
        }
    }
}

/// <summary>
/// Reads one instance's events pipe: subscribes, then reads lines until the instance closes it, goes silent, or cannot be reached.
/// The caller decides about retries (see <see cref="RunAsync"/>).
/// </summary>
public static class InstanceEventsClient
{
    private static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30)];

    /// <summary>One connection. <paramref name="silence"/> is how long a line may be missing before the session counts as silent (the protocol sends a heartbeat every 15 s, so the tray uses 45 s).</summary>
    public static async Task<SessionEnd> RunOnceAsync(string pipeName, Action<EventLine> onEvent, TimeSpan silence, CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)ConnectLimit.TotalMilliseconds, ct);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return SessionEnd.Refused;
        }

        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var subscribe = JsonSerializer.Serialize(new { action = "subscribe", args = new { events = InstanceEvents.Subscribed } });
        await writer.WriteLineAsync(subscribe.AsMemory(), ct);

        var reply = await ReadLineWithinAsync(reader, silence, ct);
        if (reply is null || !reply.Contains("\"ok\":true", StringComparison.Ordinal))
        {
            return reply is null ? SessionEnd.Silent : SessionEnd.Refused;
        }

        while (true)
        {
            var line = await ReadLineWithinAsync(reader, silence, ct);
            if (line is null)
            {
                return SessionEnd.Silent;
            }

            var e = InstanceEvents.Parse(line);
            if (e is null)
            {
                continue;
            }

            if (e.Name == "closed")
            {
                return SessionEnd.Closed;
            }

            if (e.Name != "heartbeat")
            {
                onEvent(e);
            }
        }
    }

    /// <summary>Keeps a subscription up: runs sessions one after another, backing off after each one that did not succeed.</summary>
    public static async Task RunAsync(string pipeName, Action<EventLine> onEvent, TimeSpan silence, Action<SessionEnd> onEnd, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var end = await RunOnceAsync(pipeName, onEvent, silence, ct);
            onEnd(end);
            attempt = end == SessionEnd.Closed ? 0 : attempt + 1;
            await Task.Delay(Backoff[Math.Min(attempt, Backoff.Length - 1)], ct);
        }
    }

    /// <summary>The next line, or null when none comes within the limit. Cancellation is not a null.</summary>
    private static async Task<string?> ReadLineWithinAsync(StreamReader reader, TimeSpan limit, CancellationToken ct)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(limit);
        try
        {
            return await reader.ReadLineAsync(timer.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
