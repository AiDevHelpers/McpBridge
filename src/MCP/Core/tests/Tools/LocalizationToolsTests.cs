using System.Text.Json;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class LocalizationToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loc-tools-" + Guid.NewGuid().ToString("N"));

    public LocalizationToolsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void ResxCheck_ReportsADuplicate()
    {
        Write("H/src/Resources/Strings.resx",
            "<root><data name=\"One\"><value>a</value></data><data name=\"One\"><value>b</value></data></root>");
        Write("H/src/X.cs", "var a = Strings.Get(\"One\");\n");

        using var doc = JsonDocument.Parse(LocalizationTools.ResxCheck(_root));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        var finding = doc.RootElement.GetProperty("findings")[0];
        Assert.Equal("duplicate", finding.GetProperty("Kind").GetString());
        Assert.Equal("One", finding.GetProperty("Key").GetString());
    }

    [Fact]
    public void StringScan_ReportsALiteral()
    {
        Write("A.cs", "var a = \"Could not read the file.\";\n");

        using var doc = JsonDocument.Parse(LocalizationTools.StringScan(_root));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("findings")[0].GetProperty("Line").GetInt32());
    }

    [Fact]
    public void BothToolsReturnOkFalseForAMissingFolder()
    {
        var missing = Path.Combine(_root, "nope");

        foreach (var reply in new[] { LocalizationTools.StringScan(missing), LocalizationTools.ResxCheck(missing) })
        {
            using var doc = JsonDocument.Parse(reply);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains(missing, doc.RootElement.GetProperty("error").GetString());
        }
    }
}
