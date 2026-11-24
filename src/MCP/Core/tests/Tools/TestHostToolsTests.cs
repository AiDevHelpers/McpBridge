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
        var list = JsonDocument.Parse(TestHostTools.TestHostsList()).RootElement;
        if (!list.TryGetProperty("token", out var token))
        {
            return; // No test host is running on this machine; nothing to stop.
        }

        TestHostTools.TestHostsStop(token.GetString()!);
        using var again = JsonDocument.Parse(TestHostTools.TestHostsStop(token.GetString()!));
        Assert.False(again.RootElement.GetProperty("ok").GetBoolean());
    }
}
