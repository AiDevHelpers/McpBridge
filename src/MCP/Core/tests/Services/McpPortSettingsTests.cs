using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class McpPortSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AiDevHelpers.Tests", Guid.NewGuid().ToString("N"));

    public McpPortSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string json)
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void AMissingFileGivesTheDefaultPort()
    {
        Assert.Equal(5058, McpPortSettings.Read(Path.Combine(_dir, "none.json")));
    }

    [Fact]
    public void AValidPortIsRead()
    {
        Assert.Equal(6000, McpPortSettings.Read(Write("""{"port": 6000}""")));
    }

    [Theory]
    [InlineData("""{"port": 80}""")]
    [InlineData("""{"port": 70000}""")]
    [InlineData("""{"port": "6000"}""")]
    [InlineData("""{"other": 6000}""")]
    [InlineData("not json")]
    public void AnUnusableFileGivesTheDefaultPort(string json)
    {
        Assert.Equal(5058, McpPortSettings.Read(Write(json)));
    }
}
