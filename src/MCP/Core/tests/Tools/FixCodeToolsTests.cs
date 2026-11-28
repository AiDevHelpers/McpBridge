using System.Text.Json;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

[Collection("FixCodeTools")]
public sealed class FixCodeToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fixcode-" + Guid.NewGuid().ToString("N"));

    public FixCodeToolsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        FixCodeTools.IdeCall = null;
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // a locked file in the temp folder is not a test failure
        }
    }

    private List<(string Action, string Json)> IdeCalls()
    {
        var calls = new List<(string, string)>();
        FixCodeTools.IdeCall = (action, args, _) =>
        {
            calls.Add((action, JsonSerializer.Serialize(args)));
            return Task.FromResult("{\"ok\":true}");
        };
        return calls;
    }

    private string EditorConfig => Path.Combine(_root, ".editorconfig");

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);

    [Fact]
    public async Task Enable_analysis_plan_writes_nothing()
    {
        File.WriteAllText(EditorConfig, "root = true\n");

        using var doc = Parse(await FixCodeTools.FixCode("enable-analysis", _root));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("dotnet_diagnostic.IDE0005.severity", doc.RootElement.GetProperty("block").GetString());
        Assert.Equal("root = true\n", File.ReadAllText(EditorConfig));
    }

    [Fact]
    public async Task Enable_analysis_confirm_adds_the_block_and_keeps_the_existing_text()
    {
        File.WriteAllText(EditorConfig, "root = true\n");

        using var doc = Parse(await FixCodeTools.FixCode("enable-analysis", _root, confirm: true));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var text = File.ReadAllText(EditorConfig);
        Assert.StartsWith("root = true", text);
        Assert.Contains("# BEGIN AiDevHelpers managed analysis", text);
        Assert.Contains("# END AiDevHelpers managed analysis", text);
    }

    [Fact]
    public async Task Enable_analysis_twice_leaves_one_block()
    {
        File.WriteAllText(EditorConfig, "root = true\n");

        await FixCodeTools.FixCode("enable-analysis", _root, confirm: true);
        await FixCodeTools.FixCode("enable-analysis", _root, confirm: true);

        var text = File.ReadAllText(EditorConfig);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "BEGIN AiDevHelpers managed analysis"));
    }

    [Fact]
    public async Task Enable_analysis_without_an_editorconfig_is_refused()
    {
        using var doc = Parse(await FixCodeTools.FixCode("enable-analysis", _root, confirm: true));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(File.Exists(EditorConfig));
    }

    [Fact]
    public async Task Syntax_fix_is_refused_and_names_the_existing_tool()
    {
        using var doc = Parse(await FixCodeTools.FixCode("fix-syntax", _root, confirm: true));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("ide_code_cleanup", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Format_without_confirm_calls_no_ide()
    {
        var calls = IdeCalls();

        using var doc = Parse(await FixCodeTools.FixCode("format", _root, path: "src/Thing.cs"));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Format_with_confirm_asks_the_ide_to_format_the_document()
    {
        var calls = IdeCalls();

        using var doc = Parse(await FixCodeTools.FixCode("format", _root, path: "src/Thing.cs", confirm: true));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var call = Assert.Single(calls);
        Assert.Equal("formatDocument", call.Action);
        Assert.Contains("Thing.cs", call.Json);
    }

    [Fact]
    public async Task A_path_outside_the_root_is_refused_for_format()
    {
        IdeCalls();

        using var doc = Parse(await FixCodeTools.FixCode("format", _root, path: "../escape.cs", confirm: true));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_operation_is_refused()
    {
        using var doc = Parse(await FixCodeTools.FixCode("rewrite-everything", _root));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }
}
