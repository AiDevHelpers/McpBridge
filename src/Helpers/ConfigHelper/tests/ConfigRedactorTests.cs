using AiDevHelpers.Helpers.ConfigHelper;
using Xunit;

namespace AiDevHelpers.Helpers.ConfigHelper.Tests;

public sealed class ConfigRedactorTests
{
    [Fact]
    public void RedactsJsonSecretValues()
    {
        const string json = """{ "ConnectionStrings": { "Db": "x" }, "ApiKey": "abc123", "Name": "app" }""";

        var result = ConfigRedactor.Redact(json);

        Assert.DoesNotContain("abc123", result);
        Assert.Contains("\"ApiKey\": \"***\"", result);
        Assert.Contains("\"Name\": \"app\"", result);
    }

    [Fact]
    public void RedactsXmlElementSecretValues()
    {
        const string xml = "<Settings><Password>hunter2</Password><Port>4024</Port></Settings>";

        var result = ConfigRedactor.Redact(xml);

        Assert.DoesNotContain("hunter2", result);
        Assert.Contains("<Password>***</Password>", result);
        Assert.Contains("<Port>4024</Port>", result);
    }

    [Fact]
    public void RedactsXmlAttributeSecretValues()
    {
        const string xml = "<Debugger Token=\"t0k3n\" Host=\"box\" />";

        var result = ConfigRedactor.Redact(xml);

        Assert.DoesNotContain("t0k3n", result);
        Assert.Contains("Host=\"box\"", result);
    }

    [Fact]
    public void LeavesNonSecretTextUnchanged()
    {
        const string text = "{ \"Logging\": { \"LogLevel\": \"Information\" } }";

        Assert.Equal(text, ConfigRedactor.Redact(text));
    }
}
