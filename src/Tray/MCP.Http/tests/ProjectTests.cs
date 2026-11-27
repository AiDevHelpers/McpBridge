using Xunit;

namespace AiDevHelpers.MCP.Http.Tests;

public sealed class ProjectTests
{
    [Fact]
    public void AssemblyNameMatchesTheProject()
    {
        Assert.Equal("AiDevHelpers.MCP.Http", typeof(ProjectMarker).Assembly.GetName().Name);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("localhost", true)]
    [InlineData("[::1]", true)]
    [InlineData("evil.example", false)]
    [InlineData("127.0.0.1.evil.example", false)]
    public void HostAllowsOnlyLoopbackNames(string host, bool allowed)
    {
        Assert.Equal(allowed, HttpGuard.HostAllowed(host));
    }

    [Fact]
    public void PortConflictReportNamesThePortAndTheCause()
    {
        var json = PortConflictReport.Json(5058, "address in use");

        Assert.Contains("\"state\":\"portConflict\"", json, StringComparison.Ordinal);
        Assert.Contains("\"port\":5058", json, StringComparison.Ordinal);
        Assert.Contains("address in use", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost:5058", true)]
    [InlineData("http://127.0.0.1:5058", true)]
    [InlineData("https://evil.example", false)]
    [InlineData("null", false)]
    public void OriginAllowsOnlyLoopback(string origin, bool allowed)
    {
        Assert.Equal(allowed, HttpGuard.OriginAllowed(origin));
    }
}
