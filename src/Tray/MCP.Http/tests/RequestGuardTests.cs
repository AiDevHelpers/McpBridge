using Xunit;

namespace AiDevHelpers.MCP.Http.Tests;

public sealed class RequestGuardTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private const int Port = 5000;

    [Theory]
    [InlineData("127.0.0.1:5000")]
    [InlineData("localhost:5000")]
    [InlineData("LOCALHOST:5000")]
    public void LoopbackHostWithTheRightPortIsAllowed(string host)
    {
        Assert.Equal(GuardResult.Allowed, RequestGuard.Check(host, null, "Bearer " + Token, Port, Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("evil.example:5000")]
    [InlineData("127.0.0.1:6000")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1.evil.example:5000")]
    public void OtherHostsAreRejected(string? host)
    {
        Assert.Equal(GuardResult.BadHost, RequestGuard.Check(host, null, "Bearer " + Token, Port, Token));
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("http://localhost:3000")]
    [InlineData("null")]
    public void AnyOriginHeaderIsRejected(string origin)
    {
        Assert.Equal(GuardResult.BadOrigin, RequestGuard.Check("127.0.0.1:5000", origin, "Bearer " + Token, Port, Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer ")]
    [InlineData("Bearer wrong")]
    [InlineData("Basic " + Token)]
    [InlineData(Token)]
    public void AMissingOrWrongTokenIsUnauthorized(string? authorization)
    {
        Assert.Equal(GuardResult.Unauthorized, RequestGuard.Check("127.0.0.1:5000", null, authorization, Port, Token));
    }

    [Fact]
    public void TheHostCheckRunsBeforeTheTokenCheck()
    {
        Assert.Equal(GuardResult.BadHost, RequestGuard.Check("evil.example:5000", null, null, Port, Token));
    }
}
