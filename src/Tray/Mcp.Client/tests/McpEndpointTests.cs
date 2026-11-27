using Xunit;

namespace AiDevHelpers.Mcp.Client.Tests;

public sealed class McpEndpointTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "endpoint-" + Guid.NewGuid().ToString("N"));

    public McpEndpointTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void ANoEndpointFileIsMissing()
    {
        Assert.Equal(EndpointReadKind.Missing, McpEndpointFiles.Read(_folder).Kind);
    }

    [Fact]
    public void ARecordAndATokenAreRead()
    {
        File.WriteAllText(Path.Combine(_folder, "endpoint.json"), "{\"pid\":42,\"port\":5757,\"version\":\"1.2.3.4\",\"startedUtc\":\"2026-11-27T00:00:00Z\"}");
        File.WriteAllText(Path.Combine(_folder, "token"), "abc123\n");

        var result = McpEndpointFiles.Read(_folder);

        Assert.Equal(EndpointReadKind.Found, result.Kind);
        Assert.Equal(42, result.Endpoint!.Pid);
        Assert.Equal(5757, result.Endpoint.Port);
        Assert.Equal("1.2.3.4", result.Endpoint.Version);
        Assert.Equal("abc123", result.Endpoint.Token);
        Assert.Null(result.Endpoint.Error);
    }

    [Fact]
    public void AStartupErrorInTheRecordIsCarried()
    {
        File.WriteAllText(Path.Combine(_folder, "endpoint.json"), "{\"pid\":42,\"port\":5757,\"version\":\"1\",\"error\":\"port-in-use\"}");

        var result = McpEndpointFiles.Read(_folder);

        Assert.Equal(EndpointReadKind.Found, result.Kind);
        Assert.Equal("port-in-use", result.Endpoint!.Error);
        Assert.Equal(string.Empty, result.Endpoint.Token);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"pid\":1}")]
    public void AnUnusableRecordIsInvalid(string content)
    {
        File.WriteAllText(Path.Combine(_folder, "endpoint.json"), content);

        Assert.Equal(EndpointReadKind.Invalid, McpEndpointFiles.Read(_folder).Kind);
    }
}
