using System.Linq;
using System.Text.Json;
using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class TestSummaryCapperTests
{
    [Fact]
    public void Counts_KeptWhole()
    {
        var summary = JsonDocument.Parse("{\"ok\":true,\"total\":5,\"passed\":3,\"failed\":2}").RootElement;
        var capped = (System.Text.Json.Nodes.JsonObject)TestSummaryCapper.Cap(summary);
        Assert.Equal(2, (int)capped["failed"]!);
        Assert.Equal(5, (int)capped["total"]!);
    }

    [Fact]
    public void LongFailureList_CappedWithOmittedCount()
    {
        var items = string.Join(",", Enumerable.Range(1, 25).Select(i => $"\"t{i}\""));
        var summary = JsonDocument.Parse($"{{\"failures\":[{items}]}}").RootElement;
        var capped = (System.Text.Json.Nodes.JsonObject)TestSummaryCapper.Cap(summary);
        Assert.Equal(20, capped["failures"]!.AsArray().Count);
        Assert.Equal(5, (int)capped["failures_omitted"]!);
    }

    [Fact]
    public void LongMessage_Cut()
    {
        var text = new string('m', 500);
        var summary = JsonDocument.Parse($"{{\"failure_details\":[{{\"name\":\"t\",\"message\":\"{text}\"}}]}}").RootElement;
        var capped = TestSummaryCapper.Cap(summary);
        var message = (string)capped["failure_details"]![0]!["message"]!;
        Assert.Equal(201, message.Length);
    }
}
