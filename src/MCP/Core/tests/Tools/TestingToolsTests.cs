using System.Text.Json;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class TestingToolsTests
{
    [Fact]
    public void TestWatch_UnknownIdeSetting_Throws()
    {
        Assert.Throws<ArgumentException>(() => TestingTools.TestWatch(@"C:\__testing_no_such__\App.sln", "maybe"));
    }

    [Fact]
    public void TestWatch_IdeOn_ReportsIdeAsSource()
    {
        var sln = $@"C:\__testing_{Guid.NewGuid():N}\App.sln";
        using var doc = JsonDocument.Parse(TestingTools.TestWatch(sln, "on", dotnetWatchRunning: true));

        var state = doc.RootElement.GetProperty("state");
        Assert.Equal("Ide", state.GetProperty("source").GetString());
        Assert.True(state.GetProperty("active").GetBoolean());
        TestingTools.TestUnwatch(sln);
    }

    [Fact]
    public void TestUnwatch_NotWatched_ReportsFalse()
    {
        using var doc = JsonDocument.Parse(TestingTools.TestUnwatch(@"C:\__testing_never__\App.sln"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void TestFailures_EmptyQueue_ReturnsZero()
    {
        using var doc = JsonDocument.Parse(TestingTools.TestFailures());

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task TestCoverage_UnknownProvider_ReportsError()
    {
        using var doc = JsonDocument.Parse(await TestingTools.TestCoverage(@"C:\__testing_none__\App.sln", "Nope"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }
}
