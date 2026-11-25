using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class EventFilterTests
{
    private static BufferedEvent Evt(long seq, string name = "buildDone", string data = "null") =>
        new(seq, name, $"{{\"event\":\"{name}\",\"seq\":{seq},\"data\":{data}}}");

    private static List<BufferedEvent> Buffer(int count) =>
        Enumerable.Range(1, count).Select(i => Evt(i)).ToList();

    [Fact]
    public void Apply_NoNames_ReturnsAllAfterSinceSeq()
    {
        var result = EventFilter.Apply(Buffer(3), 1, new EventFilterOptions());
        Assert.Equal(new long[] { 2, 3 }, result.Events.Select(e => (long)e!["seq"]!).ToArray());
    }

    [Fact]
    public void Apply_Names_KeepsOnlyListed()
    {
        var buffer = new[] { Evt(1, "buildBegin"), Evt(2, "buildDone"), Evt(3, "documentSaved") };
        var result = EventFilter.Apply(buffer, 0, new EventFilterOptions(Names: new[] { "buildDone" }));
        Assert.Equal(new[] { "buildDone" }, result.Events.Select(e => (string)e!["event"]!).ToArray());
    }

    [Fact]
    public void Apply_EmptyNames_MeansAll()
    {
        var result = EventFilter.Apply(Buffer(2), 0, new EventFilterOptions(Names: new List<string>()));
        Assert.Equal(2, result.Events.Count);
    }

    [Fact]
    public void Apply_Limit_ReturnsOldestAndCountsOmitted()
    {
        var result = EventFilter.Apply(Buffer(5), 0, new EventFilterOptions(Limit: 2));
        Assert.Equal(new long[] { 1, 2 }, result.Events.Select(e => (long)e!["seq"]!).ToArray());
        Assert.Equal(3, result.Omitted);
        Assert.Equal(2, result.NextSeq);
    }

    [Fact]
    public void Apply_NoOverflow_NextSeqIsLastBuffered()
    {
        var result = EventFilter.Apply(Buffer(3), 0, new EventFilterOptions());
        Assert.Equal(0, result.Omitted);
        Assert.Equal(3, result.NextSeq);
    }

    [Fact]
    public void Apply_LimitZero_ActsAsOne()
    {
        var result = EventFilter.Apply(Buffer(4), 0, new EventFilterOptions(Limit: 0));
        Assert.Single(result.Events);
    }

    [Fact]
    public void Apply_LimitAboveMax_ActsAsMax()
    {
        var result = EventFilter.Apply(Buffer(250), 0, new EventFilterOptions(Limit: 9999));
        Assert.Equal(EventFilter.MaxLimit, result.Events.Count);
    }

    [Fact]
    public void Apply_LongString_TruncatedWithMarker()
    {
        var longPath = new string('a', 1000);
        var buffer = new[] { Evt(1, "documentSaved", $"{{\"file\":\"{longPath}\"}}") };
        var result = EventFilter.Apply(buffer, 0, new EventFilterOptions(MaxTextChars: 50));
        var data = (JsonObject)result.Events[0]!["data"]!;
        Assert.Equal(51, ((string)data["file"]!).Length);
        Assert.EndsWith("…", (string)data["file"]!);
        Assert.True((bool)data["truncated"]!);
    }

    [Fact]
    public void Apply_ShortStrings_NotMarked()
    {
        var buffer = new[] { Evt(1, "documentSaved", "{\"file\":\"short.cs\"}") };
        var result = EventFilter.Apply(buffer, 0, new EventFilterOptions(MaxTextChars: 50));
        var data = (JsonObject)result.Events[0]!["data"]!;
        Assert.False(data.ContainsKey("truncated"));
    }

    [Fact]
    public void Apply_NestedLongString_Truncated()
    {
        var longText = new string('b', 500);
        var buffer = new[] { Evt(1, "buildDone", $"{{\"errors\":[{{\"message\":\"{longText}\"}}]}}") };
        var result = EventFilter.Apply(buffer, 0, new EventFilterOptions(MaxTextChars: 40));
        var message = (string)result.Events[0]!["data"]!["errors"]![0]!["message"]!;
        Assert.Equal(41, message.Length);
    }

    [Fact]
    public void Apply_NullData_Passes()
    {
        var buffer = new[] { Evt(1, "solutionClosing", "null") };
        var result = EventFilter.Apply(buffer, 0, new EventFilterOptions());
        Assert.Null(result.Events[0]!["data"]);
    }

    [Fact]
    public void Apply_EmptyBuffer_ReturnsEmpty()
    {
        var result = EventFilter.Apply(new List<BufferedEvent>(), 5, new EventFilterOptions());
        Assert.Empty(result.Events);
        Assert.Equal(5, result.NextSeq);
    }

    [Fact]
    public void Apply_MinSeverity_Info_KeepsEverything()
    {
        var result = EventFilter.Apply(Buffer(2), 0, new EventFilterOptions(MinSeverity: EventSeverity.Info));
        Assert.Equal(2, result.Events.Count);
    }
}
