using System;
using System.Collections.Generic;
using System.Text.Json;
using AiDevHelpers.Bridge;
using Xunit;

namespace AiDevHelpers.Bridge.Tests;

public sealed class EventHubTests
{
    private static JsonElement Parse(string line) => JsonDocument.Parse(line).RootElement.Clone();

    [Fact]
    public void Subscriber_receives_published_events_in_order_with_sequence_numbers()
    {
        var hub = new EventHub();
        var subscriber = hub.Subscribe(null);

        hub.Publish("buildBegin", new { scope = "solution" });
        hub.Publish("buildDone", null);

        var first = Parse(subscriber.Next()!);
        var second = Parse(subscriber.Next()!);
        Assert.Equal("buildBegin", first.GetProperty("event").GetString());
        Assert.Equal(1, first.GetProperty("seq").GetInt64());
        Assert.Equal("buildDone", second.GetProperty("event").GetString());
        Assert.Equal(2, second.GetProperty("seq").GetInt64());
        Assert.Null(subscriber.Next());
    }

    [Fact]
    public void Subscriber_with_names_receives_only_those_events()
    {
        var hub = new EventHub();
        var subscriber = hub.Subscribe(new[] { "buildDone" });

        hub.Publish("buildBegin", null);
        hub.Publish("buildDone", null);

        Assert.Equal("buildDone", Parse(subscriber.Next()!).GetProperty("event").GetString());
        Assert.Null(subscriber.Next());
    }

    [Fact]
    public void Full_queue_drops_oldest_and_reports_the_drop_count_first()
    {
        var hub = new EventHub();
        var subscriber = hub.Subscribe(null);

        for (var i = 0; i < EventSubscriber.Capacity + 3; i++)
        {
            hub.Publish("documentSaved", new { i });
        }

        var dropped = Parse(subscriber.Next()!);
        Assert.Equal("dropped", dropped.GetProperty("event").GetString());
        Assert.Equal(3, dropped.GetProperty("data").GetProperty("count").GetInt32());

        var oldest = Parse(subscriber.Next()!);
        Assert.Equal(3, oldest.GetProperty("data").GetProperty("i").GetInt32());
    }

    [Fact]
    public void Unsubscribed_subscriber_gets_no_more_events_and_the_reason()
    {
        var hub = new EventHub();
        var subscriber = hub.Subscribe(null);

        hub.Unsubscribe(subscriber, "unsubscribed");
        hub.Publish("buildDone", null);

        Assert.True(subscriber.IsClosed);
        Assert.Equal("unsubscribed", subscriber.CloseReason);
        Assert.Null(subscriber.Next());
        Assert.Equal(0, hub.Count);
    }

    [Fact]
    public void CloseAll_closes_every_subscriber_with_the_reason()
    {
        var hub = new EventHub();
        var a = hub.Subscribe(null);
        var b = hub.Subscribe(null);

        hub.CloseAll("bridgeStopped");

        Assert.Equal("bridgeStopped", a.CloseReason);
        Assert.Equal("bridgeStopped", b.CloseReason);
        Assert.Equal(0, hub.Count);
    }

    [Fact]
    public void Heartbeat_uses_the_next_sequence_number()
    {
        var hub = new EventHub();
        var subscriber = hub.Subscribe(null);
        hub.Publish("buildBegin", null);
        subscriber.Next();

        var heartbeat = Parse(subscriber.Heartbeat());
        Assert.Equal("heartbeat", heartbeat.GetProperty("event").GetString());
        Assert.Equal(2, heartbeat.GetProperty("seq").GetInt64());
    }
}
