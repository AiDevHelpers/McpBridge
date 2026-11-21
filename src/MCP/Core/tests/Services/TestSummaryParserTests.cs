using AiDevHelpers.MCP.Core.Services;
using System.Text.Json;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public class TestSummaryParserTests
{
    [Fact]
    public void ParseLatest_ReadsTheNewestSummaryLine()
    {
        const string raw = """
            {"ok":true,"result":{"pattern":"x","match_count":2,"lines":[
              "========== Test run finished: 9149 Tests (9144 Passed, 3 Failed, 2 Skipped) run in 2.6 min ==========",
              "========== Test run finished: 9149 Tests (9146 Passed, 1 Failed, 2 Skipped) run in 2.4 min =========="
            ]}}
            """;

        using var json = JsonDocument.Parse(TestSummaryParser.ParseLatest(raw));

        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(9149, json.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(9146, json.RootElement.GetProperty("passed").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("failed").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("skipped").GetInt32());
    }

    [Fact]
    public void ParseLatest_NoSummaryLine_ReportsNotOk()
    {
        const string raw = """{"ok":true,"result":{"lines":["Starting test run"]}}""";

        using var json = JsonDocument.Parse(TestSummaryParser.ParseLatest(raw));

        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("No finished test run", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void ParseLatest_NotJson_ReportsNotOk()
    {
        using var json = JsonDocument.Parse(TestSummaryParser.ParseLatest("not json"));

        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
    }
}
