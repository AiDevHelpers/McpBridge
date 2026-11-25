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
/// Holds the events-pipe connections this MCP server has open to Visual Studio bridges. Each subscription has a background
/// reader that keeps the most recent events (bounded) so a caller can read them later by sequence number.
/// Protocol: conformance/protocol.md, "Events pipe".
/// </summary>
public static class EventSubscriptions
{
    /// <summary>Subscriptions allowed per bridge (the bridge accepts this many concurrent subscribers).</summary>
    public const int MaxPerBridge = 8;

    /// <summary>Events kept per subscription; older ones are dropped.</summary>
    public const int BufferLimit = 500;

    private static readonly object Lock = new();
    private static readonly Dictionary<string, Subscription> Subscriptions = new();

    public static async Task<string> SubscribeAsync(int pid, IReadOnlyList<string>? names, CancellationToken ct)
    {
        lock (Lock)
        {
            if (Subscriptions.Values.Count(s => s.Pid == pid && !s.Closed) >= MaxPerBridge)
            {
                throw new InvalidOperationException($"This bridge already has {MaxPerBridge} subscriptions. Unsubscribe one first.");
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

        var request = JsonSerializer.Serialize(new { action = "subscribe", args = new { events = names ?? (IReadOnlyList<string>)Array.Empty<string>() } });
        var reader = new StreamReader(client, Encoding.UTF8, false, 4096, leaveOpen: true);
        var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(request).ConfigureAwait(false);
        var replyLine = await reader.ReadLineAsync().ConfigureAwait(false) ?? throw new IOException("The bridge closed the events pipe.");
        using var reply = JsonDocument.Parse(replyLine);
        if (!reply.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            client.Dispose();
            var message = reply.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "subscribe refused";
            throw new InvalidOperationException(message);
        }

        var subscriptionId = reply.RootElement.GetProperty("subscriptionId").GetString()!;
        var subscription = new Subscription(subscriptionId, pid, client);
        lock (Lock)
        {
            Subscriptions[subscriptionId] = subscription;
        }

        subscription.Reader = Task.Run(() => ReadLoopAsync(subscription, reader));
        return subscriptionId;
    }

    /// <summary>Events with a sequence number after <paramref name="sinceSeq"/>, plus whether the subscription has ended.</summary>
    public static object Read(string subscriptionId, long sinceSeq)
    {
        var subscription = Find(subscriptionId);
        lock (subscription.Gate)
        {
            var events = subscription.Buffer.Where(e => e.Seq > sinceSeq).Select(e => e.Json).ToList();
            return new
            {
                subscription_id = subscriptionId,
                pid = subscription.Pid,
                events = events.Select(json => JsonSerializer.Deserialize<JsonElement>(json)).ToList(),
                last_seq = subscription.Buffer.Count == 0 ? 0 : subscription.Buffer[^1].Seq,
                closed = subscription.Closed,
                close_reason = subscription.CloseReason,
            };
        }
    }

    public static void Unsubscribe(string subscriptionId)
    {
        Subscription? subscription;
        lock (Lock)
        {
            if (!Subscriptions.Remove(subscriptionId, out subscription))
            {
                throw new KeyNotFoundException($"No subscription '{subscriptionId}'.");
            }
        }

        subscription.Close("unsubscribed");
    }

    private static Subscription Find(string subscriptionId)
    {
        lock (Lock)
        {
            return Subscriptions.TryGetValue(subscriptionId, out var subscription)
                ? subscription
                : throw new KeyNotFoundException($"No subscription '{subscriptionId}'.");
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
                var name = root.TryGetProperty("event", out var evt) ? evt.GetString() : null;
                if (name == "heartbeat")
                {
                    continue;
                }

                var seq = root.TryGetProperty("seq", out var seqElement) ? seqElement.GetInt64() : 0;
                if (name == "closed")
                {
                    var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
                    subscription.Close(reason ?? "closed");
                    return;
                }

                subscription.Add(seq, line);
            }
        }
        catch (Exception)
        {
            subscription.Close("disconnected");
        }
    }

    private sealed class Subscription
    {
        public Subscription(string id, int pid, NamedPipeClientStream client)
        {
            Id = id;
            Pid = pid;
            Client = client;
        }

        public object Gate { get; } = new();

        public string Id { get; }

        public int Pid { get; }

        public NamedPipeClientStream Client { get; }

        public Task? Reader { get; set; }

        public List<(long Seq, string Json)> Buffer { get; } = new();

        public bool Closed { get; private set; }

        public string? CloseReason { get; private set; }

        public void Add(long seq, string json)
        {
            lock (Gate)
            {
                Buffer.Add((seq, json));
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
