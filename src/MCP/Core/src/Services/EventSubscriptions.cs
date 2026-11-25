using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// One events-pipe subscription per running Visual Studio instance, held by this MCP server. Each has a background reader
/// that keeps the most recent events (bounded). <see cref="EventAutoSubscribeService"/> keeps them up to date. Callers read
/// by instance process id and sequence number. Protocol: conformance/protocol.md, "Events pipe".
/// </summary>
public static class EventSubscriptions
{
    /// <summary>Events kept per instance; older ones are dropped.</summary>
    public const int BufferLimit = 500;

    private static readonly object Lock = new();
    private static readonly Dictionary<int, Subscription> ByPid = new();

    /// <summary>Connects to the instance's events pipe if this server has no open subscription to it.</summary>
    public static async Task<bool> EnsureSubscribedAsync(int pid, CancellationToken ct)
    {
        lock (Lock)
        {
            if (ByPid.TryGetValue(pid, out var existing) && !existing.Closed)
            {
                return false;
            }
        }

        var client = new NamedPipeClientStream(".", $"AiDevHelpers.VS.{pid}.events", PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(5000, ct).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        var reader = new StreamReader(client, Encoding.UTF8, false, 4096, leaveOpen: true);
        var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync("{\"action\":\"subscribe\",\"args\":{}}").ConfigureAwait(false);
        var replyLine = await reader.ReadLineAsync().ConfigureAwait(false) ?? throw new IOException("The bridge closed the events pipe.");
        using (var reply = JsonDocument.Parse(replyLine))
        {
            if (!reply.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                client.Dispose();
                var message = reply.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "subscribe refused";
                throw new InvalidOperationException(message);
            }
        }

        var subscription = new Subscription(pid, client);
        lock (Lock)
        {
            ByPid[pid] = subscription;
        }

        subscription.Reader = Task.Run(() => ReadLoopAsync(subscription, reader));
        return true;
    }

    /// <summary>Instances this server is subscribed to and still connected to.</summary>
    public static IReadOnlyList<int> ActivePids()
    {
        lock (Lock)
        {
            return ByPid.Values.Where(s => !s.Closed).Select(s => s.Pid).ToList();
        }
    }

    /// <summary>
    /// Events for one instance with a sequence number after <paramref name="sinceSeq"/>, optionally only the named
    /// events. Also reports whether the subscription has ended and why.
    /// </summary>
    public static object Read(int pid, long sinceSeq, IReadOnlyCollection<string>? names)
    {
        var subscription = Find(pid);
        lock (subscription.Gate)
        {
            var wanted = names is null || names.Count == 0 ? null : new HashSet<string>(names, StringComparer.Ordinal);
            var events = subscription.Buffer
                .Where(e => e.Seq > sinceSeq && (wanted is null || wanted.Contains(e.Name)))
                .Select(e => JsonSerializer.Deserialize<JsonElement>(e.Json))
                .ToList();
            return new
            {
                pid,
                events,
                last_seq = subscription.Buffer.Count == 0 ? 0 : subscription.Buffer[^1].Seq,
                closed = subscription.Closed,
                close_reason = subscription.CloseReason,
            };
        }
    }

    public static void Unsubscribe(int pid)
    {
        Subscription? subscription;
        lock (Lock)
        {
            if (!ByPid.Remove(pid, out subscription))
            {
                throw new KeyNotFoundException($"No events subscription to instance {pid}.");
            }
        }

        subscription.Close("unsubscribed");
    }

    /// <summary>The one subscribed instance, when exactly one is subscribed. Used when a caller names no instance.</summary>
    public static int OnlySubscribedPid()
    {
        var pids = ActivePids();
        return pids.Count == 1
            ? pids[0]
            : throw new InvalidOperationException(pids.Count == 0
                ? "No Visual Studio instance is subscribed for events."
                : "More than one instance is subscribed. Pass instancePid.");
    }

    private static Subscription Find(int pid)
    {
        lock (Lock)
        {
            return ByPid.TryGetValue(pid, out var subscription)
                ? subscription
                : throw new KeyNotFoundException($"No events subscription to instance {pid}. Subscribe first.");
        }
    }

    private static async Task ReadLoopAsync(Subscription subscription, StreamReader reader)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    subscription.Close("disconnected");
                    return;
                }

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var name = root.TryGetProperty("event", out var evt) ? evt.GetString() ?? string.Empty : string.Empty;
                if (name == "heartbeat")
                {
                    continue;
                }

                if (name == "closed")
                {
                    var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
                    subscription.Close(reason ?? "closed");
                    return;
                }

                var seq = root.TryGetProperty("seq", out var seqElement) ? seqElement.GetInt64() : 0;
                subscription.Add(seq, name, line);
            }
        }
        catch (Exception)
        {
            subscription.Close("disconnected");
        }
    }

    private sealed class Subscription
    {
        public Subscription(int pid, NamedPipeClientStream client)
        {
            Pid = pid;
            Client = client;
        }

        public object Gate { get; } = new();

        public int Pid { get; }

        public NamedPipeClientStream Client { get; }

        public Task? Reader { get; set; }

        public List<(long Seq, string Name, string Json)> Buffer { get; } = new();

        public bool Closed { get; private set; }

        public string? CloseReason { get; private set; }

        public void Add(long seq, string name, string json)
        {
            lock (Gate)
            {
                Buffer.Add((seq, name, json));
                if (Buffer.Count > BufferLimit)
                {
                    Buffer.RemoveAt(0);
                }
            }
        }

        public void Close(string reason)
        {
            lock (Gate)
            {
                if (Closed)
                {
                    return;
                }

                Closed = true;
                CloseReason = reason;
            }

            try
            {
                Client.Dispose();
            }
            catch (Exception)
            {
                // Already gone.
            }
        }
    }
}
