using System.Linq;
using System.Reflection;
using AiDevHelpers.MCP.Core.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

/// <summary>Every helper ships a self-test, and every MCP tool of a helper is named in that helper's self-test (Q4).</summary>
public sealed class SelfTestCoverageTests
{
    // Tool class in the Core library, and the helper whose self-test must name its tools.
    private static readonly (string ToolClass, string Helper)[] ToolToHelper =
    [
        ("BookmarkTools", "BookmarkHelper"),
        ("ConfigTools", "ConfigHelper"),
        ("ErrorTools", "ErrorHelper"),
        ("ExtensionTools", "ExtensionsHelper"),
        ("LintTools", "LintHelper"),
        ("OutputTools", "OutputHelper"),
        ("GateTools", "GateHelper"),
        ("PackageTools", "PackageHelper"),
        ("ProjectTools", "ProjectHelper"),
        ("SourceControlTools", "SourceControlHelper"),
        ("TestingTools", "TestingHelper"),
        ("TerminalSessionTools", "TerminalControl"),
        ("ScriptHelperTools", "ScriptHelper"),
    ];

    [Fact]
    public void EveryHelperEmbedsASelfTest()
    {
        foreach (var helper in SelfTestTools.HelperNames)
        {
            var markdown = SelfTestTools.ReadSelfTest(helper);

            Assert.False(string.IsNullOrWhiteSpace(markdown), $"{helper} has no embedded AiDevHelpers.SelfTest.md.");
        }
    }

    [Fact]
    public void EveryMcpToolIsNamedInItsHelpersSelfTest()
    {
        var core = typeof(SelfTestTools).Assembly;

        foreach (var (toolClass, helper) in ToolToHelper)
        {
            var type = core.GetType("AiDevHelpers.MCP.Core.Tools." + toolClass, throwOnError: true)!;
            var markdown = SelfTestTools.ReadSelfTest(helper) ?? string.Empty;
            var tools = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
                .Select(m => m.Name)
                .ToList();

            Assert.NotEmpty(tools);
            foreach (var tool in tools)
            {
                Assert.Contains(tool, markdown);
            }
        }
    }

    [Fact]
    public void SelfTestListReportsEveryHelperAndTheCoreVersion()
    {
        var json = System.Text.Json.JsonDocument.Parse(SelfTestTools.SelfTestList());

        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(SelfTestTools.HelperNames.Length, json.RootElement.GetProperty("helpers").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("core_version").GetString()));
    }
}
