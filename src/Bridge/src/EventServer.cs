using System;
using System.IO;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AiDevHelpers.Bridge;

/// <summary>
/// The events pipe (`AiDevHelpers.VS.&lt;pid&gt;.events`). Separate from the request pipe, which stays one request and one
/// reply per connection. A client sends one subscribe line, gets one reply line with a subscription id, then reads event
/// lines until it closes the connection. See conformance/protocol.md, "Events pipe".
/// </summary>
public sealed class EventServer : IDisposable
{
    /// <summary>
    /// Pipe instances on the events pipe, and so concurrent subscribers. Each instance has its own accept loop.
    /// </summary>
    public const int MaxSubscribers = 8;

    /// <summary>Silence after which the writer sends a heartbeat line.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>How long one write may take before the subscriber is closed as slow.</summary>
    public static readonly TimeSpan WriteLimit = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan SubscribeLimit = TimeSpan.FromSeconds(10);

    private readonly string _pipeName;
    private readonly int _pid;
    private readonly EventHub _hub;
    private readonly IBridgeLogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _accepts = new();

    public EventServer(string pipeName, int pid, EventHub hub, IBridgeLogger logger)
    {
        _pipeName = pipeName;
        _pid = pid;
        _hub = hub;
        _logger = logger;
    }

    public void Start()
    {
        // One accept loop per pipe instance: a client that holds one instance (for example the MCP server's automatic
        // subscription) leaves the others free for further subscribers.
        for (var i = 0; i < MaxSubscribers; i++)
        {
            _accepts.Add(Task.Run(() => AcceptLoopAsync(_cts.Token)));
        }
    }

    /// <summary>
    /// Restarts any accept loop that has stopped (faulted or ended) while the server is still meant to run. Called by the
    /// bridge watchdog.
    /// </summary>
    public void EnsureRunning()
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        lock (_accepts)
        {
            for (var i = 0; i < _accepts.Count; i++)
            {
                if (_accepts[i].IsCompleted)
                {
                    _accepts[i] = Task.Run(() => AcceptLoopAsync(_cts.Token));
                }
            }
        }
    }

    public void Dispose()
    {
        _hub.CloseAll("bridgeStopped");
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        // Same shape as the request pipe (BridgeServer.RunAsync): create one instance, wait for its client, serve that client
        // to the end, dispose, then create the next. A new instance is never created while a client is being served, so a
        // single-instance pipe cannot be refused with access denied.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var stream = CreateStream();
                await stream.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await ServeAsync(stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The pipe could not be created or served. Log it and try again shortly.
                _logger.LogException(ex);
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        using (stream)
        {
            try
            {
                var subscribeLine = await ReadLineAsync(stream).WithTimeout(SubscribeLimit).ConfigureAwait(false);
                var names = ParseSubscribe(subscribeLine);
                var subscriber = _hub.Subscribe(names);
                try
                {
                    await WriteLineAsync(stream, JsonSerializer.Serialize(new { ok = true, pid = _pid, subscriptionId = subscriber.Id })).ConfigureAwait(false);
                    await WriterLoopAsync(stream, subscriber, ct).ConfigureAwait(false);
                }
                finally
                {
                    _hub.Unsubscribe(subscriber, subscriber.CloseReason ?? "unsubscribed");
                }
            }
            catch (Exception ex)
            {
                // A bad subscribe line or a dropped client only ends this connection. Log it so it can be seen.
                _logger.LogException(ex);
            }
        }
    }

    private async Task WriterLoopAsync(Stream stream, EventSubscriber subscriber, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = subscriber.Next();
            if (line is null)
            {
                if (subscriber.IsClosed)
                {
                    await WriteWithLimitAsync(stream, JsonSerializer.Serialize(new { @event = "closed", reason = subscriber.CloseReason }), subscriber, ct).ConfigureAwait(false);
                    return;
                }

                var woke = await Task.Run(() => subscriber.WaitForEvent(HeartbeatInterval), ct).ConfigureAwait(false);
                if (!woke && !subscriber.IsClosed)
                {
                    line = subscriber.Heartbeat();
                }
                else
                {
                    continue;
                }
            }

            await WriteWithLimitAsync(stream, line, subscriber, ct).ConfigureAwait(false);
        }
    }

    // A write that takes longer than WriteLimit means the client has stopped reading: close it as slow.
    private static async Task WriteWithLimitAsync(Stream stream, string line, EventSubscriber subscriber, CancellationToken ct)
    {
        var write = WriteLineAsync(stream, line);
        var finished = await Task.WhenAny(write, Task.Delay(WriteLimit, ct)).ConfigureAwait(false);
        if (finished != write)
        {
            subscriber.Close("slowSubscriber");
            throw new IOException("Subscriber did not read within the write limit.");
        }

        await write.ConfigureAwait(false);
    }

    private static Task WriteLineAsync(Stream stream, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        return stream.WriteAsync(bytes, 0, bytes.Length);
    }

    private static async Task<string> ReadLineAsync(Stream stream)
    {
        var bytes = new System.Collections.Generic.List<byte>();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, 0, 1).ConfigureAwait(false);
            if (read == 0 || one[0] == (byte)'\n')
            {
                break;
            }

            bytes.Add(one[0]);
            if (bytes.Count > 64 * 1024)
            {
                throw new InvalidDataException("Subscribe line too long.");
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
    }

    // The subscribe line: {"action":"subscribe","args":{"events":["buildDone",...]}}. No events means all events.
    private static System.Collections.Generic.IReadOnlyCollection<string>? ParseSubscribe(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("action", out var action) || action.GetString() != "subscribe")
        {
            throw new InvalidDataException("Expected a subscribe request.");
        }

        if (!root.TryGetProperty("args", out var args) || !args.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = new System.Collections.Generic.List<string>();
        foreach (var name in events.EnumerateArray())
        {
            var text = name.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                names.Add(text);
            }
        }

        return names;
    }

    // Several instances of one pipe name need the right to create a new instance (FILE_CREATE_PIPE_INSTANCE), which
    // ReadWrite does not carry. The request pipe has one instance and keeps its ReadWrite ACL (BridgeServer.CreatePipeSecurity).
    private static PipeSecurity CreateEventPipeSecurity()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User;
        if (user is not null)
        {
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private NamedPipeServerStream CreateStream()
    {
        var security = CreateEventPipeSecurity();
#if NETFRAMEWORK
        return new NamedPipeServerStream(
            _pipeName, PipeDirection.InOut, MaxSubscribers, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#else
        return NamedPipeServerStreamAcl.Create(
            _pipeName, PipeDirection.InOut, MaxSubscribers, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#endif
    }
}

internal static class TaskTimeoutExtensions
{
    public static async Task<T> WithTimeout<T>(this Task<T> task, TimeSpan limit)
    {
        var finished = await Task.WhenAny(task, Task.Delay(limit)).ConfigureAwait(false);
        if (finished != task)
        {
            throw new TimeoutException("Subscribe line not received in time.");
        }

        return await task.ConfigureAwait(false);
    }
}
