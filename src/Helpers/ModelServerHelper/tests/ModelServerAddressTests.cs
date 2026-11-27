using Xunit;

namespace AiDevHelpers.Helpers.ModelServerHelper.Tests;

public sealed class ModelServerAddressTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ms-addr-" + Guid.NewGuid().ToString("N"));

    public ModelServerAddressTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private void Config(string json) => File.WriteAllText(Path.Combine(_dir, "SimpleModelServer.config.json"), json);

    [Fact]
    public void Missing_file_uses_default() =>
        Assert.Equal("http://localhost:11435/", ModelServerAddress.Resolve(_dir).ToString());

    [Fact]
    public void Reads_loopback_host_and_port()
    {
        Config("{\"global\":{\"routerHost\":\"127.0.0.1\",\"routerPort\":12000}}");
        Assert.Equal("http://127.0.0.1:12000/", ModelServerAddress.Resolve(_dir).ToString());
    }

    [Fact]
    public void Reads_snake_case_keys()
    {
        Config("{\"global\":{\"router_host\":\"localhost\",\"router_port\":12001}}");
        Assert.Equal("http://localhost:12001/", ModelServerAddress.Resolve(_dir).ToString());
    }

    [Theory]
    [InlineData("evil.example.com")]
    [InlineData("192.168.1.5")]
    [InlineData("0.0.0.0")]
    public void Non_loopback_host_is_ignored(string host)
    {
        Config("{\"global\":{\"routerHost\":\"" + host + "\",\"routerPort\":12000}}");
        Assert.Equal("http://localhost:11435/", ModelServerAddress.Resolve(_dir).ToString());
    }

    [Fact]
    public void Malformed_file_uses_default()
    {
        Config("{ not json");
        Assert.Equal("http://localhost:11435/", ModelServerAddress.Resolve(_dir).ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void Invalid_port_uses_default_port(int port)
    {
        Config("{\"global\":{\"routerHost\":\"localhost\",\"routerPort\":" + port + "}}");
        Assert.Equal("http://localhost:11435/", ModelServerAddress.Resolve(_dir).ToString());
    }
}
