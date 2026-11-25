using System.Linq;
using System.Text.Json.Nodes;
using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class ReplySizeGuardTests
{
    [Fact]
    public void FitWithinLimit_ChangesNothing()
    {
        var node = JsonNode.Parse("{\"status\":\"ok\",\"failing\":[\"a\",\"b\"]}")!;

        var fitted = ReplySizeGuard.Fit(node, 1000);

        Assert.Equal("ok", fitted["status"]!.GetValue<string>());
        Assert.Equal(2, fitted["failing"]!.AsArray().Count);
        Assert.Null(fitted["reply_truncated"]);
    }

    [Fact]
    public void FitOverLimit_CutsLongestListAndCountsOmitted()
    {
        var items = string.Join(",", Enumerable.Range(0, 40).Select(i => "\"failure number " + i + " with some text\""));
        var node = JsonNode.Parse("{\"status\":\"failed\",\"failing\":[" + items + "]}")!;

        var fitted = ReplySizeGuard.Fit(node, 400);

        Assert.True(fitted.ToJsonString().Length <= 400);
        Assert.True(fitted["reply_truncated"]!.GetValue<bool>());
        var kept = fitted["failing"]!.AsArray().Count;
        Assert.Equal(40, kept + fitted["failing_omitted"]!.GetValue<int>());
        Assert.Equal("failed", fitted["status"]!.GetValue<string>());
    }

    [Fact]
    public void FitNeverCutsCounts()
    {
        var node = JsonNode.Parse("{\"passed\":5,\"failed\":3,\"failing\":[\"x\",\"y\",\"z\",\"w\",\"v\"]}")!;

        var fitted = ReplySizeGuard.Fit(node, 60);

        Assert.Equal(5, fitted["passed"]!.GetValue<int>());
        Assert.Equal(3, fitted["failed"]!.GetValue<int>());
    }

    [Fact]
    public void FitStopsWhenNoListLeftToCut()
    {
        var node = JsonNode.Parse("{\"status\":\"a very long status that cannot be cut by this guard at all\"}")!;

        var fitted = ReplySizeGuard.Fit(node, 10);

        Assert.Equal("a very long status that cannot be cut by this guard at all", fitted["status"]!.GetValue<string>());
    }
}
