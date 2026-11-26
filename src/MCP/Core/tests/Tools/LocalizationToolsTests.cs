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

    private void Culture(string name, string neutral, string de)
    {
        Write("P/Resources/Strings.resx", neutral);
        Write("P/Resources/Strings.de.resx", de);
    }

    private const string TwoKeys = "<root><data name=\"A\" xml:space=\"preserve\"><value>One</value></data>\n<data name=\"B\" xml:space=\"preserve\"><value>Two</value></data>\n</root>\n";
    private const string OneKeyDe = "<root><data name=\"A\" xml:space=\"preserve\"><value>Eins</value></data>\n</root>\n";

    [Fact]
    public void SyncCheck_ReturnsTheFindingShape()
    {
        Culture("de", TwoKeys, OneKeyDe);

        using var doc = JsonDocument.Parse(LocalizationTools.LocalizationSyncCheck(_root));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var f = doc.RootElement.GetProperty("findings")[0];
        Assert.Equal(("missing", "B", "de"), (f.GetProperty("kind").GetString(), f.GetProperty("key").GetString(), f.GetProperty("culture").GetString()));
    }

    [Fact]
    public void Sync_WithoutConfirmReturnsPlanAndChangesNothing()
    {
        Culture("de", TwoKeys, OneKeyDe);
        var path = Path.Combine(_root, "P/Resources/Strings.de.resx");
        var before = File.ReadAllText(path);

        using var doc = JsonDocument.Parse(LocalizationTools.LocalizationSync(_root));

        Assert.True(doc.RootElement.GetProperty("plan").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(32, doc.RootElement.GetProperty("token").GetString()!.Length);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void Sync_ConfirmApplies()
    {
        Culture("de", TwoKeys, OneKeyDe);
        using var plan = JsonDocument.Parse(LocalizationTools.LocalizationSync(_root));
        var token = plan.RootElement.GetProperty("token").GetString();

        using var applied = JsonDocument.Parse(LocalizationTools.LocalizationSync(_root, confirm: token));

        Assert.True(applied.RootElement.GetProperty("applied").GetBoolean());
        Assert.Contains("name=\"B\"", File.ReadAllText(Path.Combine(_root, "P/Resources/Strings.de.resx")));
    }

    [Fact]
    public void Sync_UnknownTokenIsOkFalse()
    {
        Culture("de", TwoKeys, OneKeyDe);

        using var doc = JsonDocument.Parse(LocalizationTools.LocalizationSync(_root, confirm: "nope"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Extract_ReturnsItemsAndSkipped()
    {
        Write("P/Strings.cs", "namespace P;\ninternal static class Strings { }\n");
        Write("P/Resources/Strings.resx", "<root></root>\n");
        Write("P/A.cs", "namespace P;\nclass A\n{\n    const string C = \"Keep this text.\";\n    string M() => \"Could not read it.\";\n}\n");

        using var doc = JsonDocument.Parse(LocalizationTools.LocalizationExtract(_root));

        Assert.True(doc.RootElement.GetProperty("plan").GetBoolean());
        var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("Strings.Get(\"CouldNotReadIt\")", item.GetProperty("replacement").GetString());
        Assert.Equal("const-context", Assert.Single(doc.RootElement.GetProperty("skipped").EnumerateArray()).GetProperty("reason").GetString());
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
