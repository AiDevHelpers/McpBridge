using System.Text.Json;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class TestHostToolsTests
{
    [Fact]
    public void StopWithUnknownTokenKillsNothing()
    {
        var json = TestHostTools.TestHostsStop("not-a-real-token");

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void ListReturnsACountAndOnlyTestHostNames()
    {
        var json = TestHostTools.TestHostsList();

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var count = doc.RootElement.GetProperty("count").GetInt32();
        if (count > 0)
        {
            foreach (var host in doc.RootElement.GetProperty("hosts").EnumerateArray())
            {
                Assert.StartsWith("testhost", host.GetProperty("name").GetString());
            }
        }
    }

    [Fact]
    public void StopTokenIsSingleUse()
    {
        // Stop with a plan that lists no processes. A real list would stop every testhost on the machine,
        // including the ones other test projects run in parallel (that killed the Bridge tests in CI).
        var token = "single-use-" + Guid.NewGuid().ToString("N");
        TestHostTools.Plans[token] = Array.Empty<TestHostTools.HostRef>();

        using var first = JsonDocument.Parse(TestHostTools.TestHostsStop(token));
        Assert.True(first.RootElement.GetProperty("ok").GetBoolean());
        using var again = JsonDocument.Parse(TestHostTools.TestHostsStop(token));
        Assert.False(again.RootElement.GetProperty("ok").GetBoolean());
    }
}
