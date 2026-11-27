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
