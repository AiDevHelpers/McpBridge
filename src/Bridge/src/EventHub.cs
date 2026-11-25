using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;

namespace AiDevHelpers.Bridge;

/// <summary>
/// Fans events out to subscribers. Each subscriber has its own bounded queue; when a subscriber falls behind, its oldest
/// events are dropped and a "dropped" event with the count is sent before the next one. Publish never blocks on a socket.
/// </summary>
public sealed class EventHub
{
    private readonly object _lock = new();
    private readonly List<EventSubscriber> _subscribers = new();

    public EventSubscriber Subscribe(IReadOnlyCollection<string>? names)
    {
        var subscriber = new EventSubscriber(names);
        lock (_lock)
        {
            _subscribers.Add(subscriber);
        }

        return subscriber;
    }

    public void Unsubscribe(EventSubscriber subscriber, string reason)
    {
        subscriber.Close(reason);
        lock (_lock)
        {
            _subscribers.Remove(subscriber);
        }
    }

    public void Publish(string name, object? data)
    {
        lock (_lock)
        {
            foreach (var subscriber in _subscribers)
            {
                subscriber.Offer(name, data);
            }
        }
    }

    public void CloseAll(string reason)
    {
        List<EventSubscriber> snapshot;
        lock (_lock)
        {
            snapshot = new List<EventSubscriber>(_subscribers);
            _subscribers.Clear();
        }

        foreach (var subscriber in snapshot)
        {
            subscriber.Close(reason);
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _subscribers.Count;
            }
        }
    }
}

/// <summary>One subscribed client: a bounded queue of encoded event lines, a sequence counter, and a wake signal.</summary>
public sealed class EventSubscriber
{
    /// <summary>Events held per subscriber before the oldest are dropped.</summary>
    public const int Capacity = 500;

    private readonly object _lock = new();
    private readonly HashSet<string>? _names;
    private readonly Queue<string> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private int _dropped;
    private long _seq;
    private bool _closed;

    public EventSubscriber(IReadOnlyCollection<string>? names)
    {
        Id = Guid.NewGuid().ToString("N");
        _names = names is null || names.Count == 0 ? null : new HashSet<string>(names, StringComparer.Ordinal);
    }

    public string Id { get; }

    public string? CloseReason { get; private set; }

    public bool IsClosed
    {
        get
        {
            lock (_lock)
            {
                return _closed;
            }
        }
    }

    /// <summary>Wakes the writer. Waits up to <paramref name="timeout"/>; returns false on timeout.</summary>
    public bool WaitForEvent(TimeSpan timeout) => _signal.Wait(timeout);

    /// <summary>Writer side: the next encoded line, or null when the queue is empty.</summary>
    public string? Next()
    {
        lock (_lock)
        {
            if (_dropped > 0)
            {
                var count = _dropped;
                _dropped = 0;
                return Encode("dropped", ++_seq, new { count });
            }

            return _queue.Count > 0 ? _queue.Dequeue() : null;
        }
    }

    /// <summary>Writer side: a heartbeat line, numbered like the other events.</summary>
    public string Heartbeat()
    {
        lock (_lock)
        {
            return Encode("heartbeat", ++_seq, null);
        }
    }

    public void Offer(string name, object? data)
    {
        if (_names is not null && !_names.Contains(name))
        {
            return;
        }

        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            if (_queue.Count >= Capacity)
            {
                _queue.Dequeue();
                _dropped++;
            }

            _queue.Enqueue(Encode(name, ++_seq, data));
        }

        _signal.Release();
    }

    public void Close(string reason)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            CloseReason = reason;
        }

        _signal.Release();
    }

    private static string Encode(string name, long seq, object? data) =>
        JsonSerializer.Serialize(new { @event = name, seq, data });
}
