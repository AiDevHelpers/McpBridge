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

    [Fact]
    public void ParseLatest_NamesTheFailuresOfTheNewestRunOnly()
    {
        const string raw = """
            {"ok":true,"result":{"lines":[
              "[xUnit.net 00:00:01.10]     Old.Suite.OldFailure [FAIL]",
              "========== Test run finished: 10 Tests (9 Passed, 1 Failed, 0 Skipped) run in 1 min ==========",
              "[xUnit.net 00:00:02.20]     My.Suite.First [FAIL]",
              "[xUnit.net 00:00:02.30]     My.Suite.Second(value: 3) [FAIL]",
              "[xUnit.net 00:00:02.40]     My.Suite.First [FAIL]",
              "========== Test run finished: 10 Tests (8 Passed, 2 Failed, 0 Skipped) run in 1 min =========="
            ]}}
            """;

        using var json = JsonDocument.Parse(TestSummaryParser.ParseLatest(raw));

        var failures = json.RootElement.GetProperty("failures").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Equal(["My.Suite.First", "My.Suite.Second(value: 3)"], failures);
        Assert.Equal(2, json.RootElement.GetProperty("failed").GetInt32());
    }

    [Fact]
    public void ParseLatest_GivesEachFailureItsMessage()
    {
        const string raw = """
            {"ok":true,"result":{"lines":[
              "[xUnit.net 00:00:02.20]     My.Suite.First [FAIL]",
              "[xUnit.net 00:00:02.21]       Assert.Equal() Failure: Values differ",
              "[xUnit.net 00:00:02.22]       Expected: 6",
              "[xUnit.net 00:00:02.23]       Actual:   5",
              "[xUnit.net 00:00:02.30]     My.Suite.Second [FAIL]",
              "========== Test run finished: 10 Tests (8 Passed, 2 Failed, 0 Skipped) run in 1 min =========="
            ]}}
            """;

        using var json = JsonDocument.Parse(TestSummaryParser.ParseLatest(raw));

        var details = json.RootElement.GetProperty("failure_details").EnumerateArray().ToList();
        Assert.Equal(2, details.Count);
        Assert.Equal("My.Suite.First", details[0].GetProperty("name").GetString());
        Assert.Equal("Assert.Equal() Failure: Values differ\nExpected: 6\nActual:   5", details[0].GetProperty("message").GetString());
        Assert.Equal(string.Empty, details[1].GetProperty("message").GetString());
    }

    [Fact]
    public void ParseLatest_NoFailLines_GivesAnEmptyFailureList()
    {
        const string raw = """
            {"ok":true,"result":{"lines":[
              "========== Test run finished: 10 Tests (10 Passed, 0 Failed, 0 Skipped) run in 1 min =========="
            ]}}
            """;

        using var json = JsonDocument.Parse(TestSummaryParser.ParseLatest(raw));

        Assert.Empty(json.RootElement.GetProperty("failures").EnumerateArray());
    }
}
