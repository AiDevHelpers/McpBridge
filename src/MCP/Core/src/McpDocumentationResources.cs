using System.ComponentModel;
using System.Reflection;

using ModelContextProtocol.Server;

using AiDevHelpers.Common.Contracts;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Exposes the embedded tool guide and full self-test checklist to MCP clients.</summary>
[McpServerResourceType]
public sealed class McpDocumentationResources
{
    private const string ToolsResource = "AiDevHelpers.Common.Resources.mcp-tools.md";
    private const string SelfTestResource = "AiDevHelpers.Common.Resources.mcp-self-test.md";

    private static string Load(string resourceName)
    {
        Assembly assembly = typeof(IIDELauncher).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found. Available: " +
                string.Join(", ", assembly.GetManifestResourceNames()));
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    [McpServerResource(
        UriTemplate = "docs://ide-bridge/mcp-tools",
        Name = "AiDevHelpers.McpBridge MCP Tools",
        MimeType = "text/markdown")]
    [Description("Guide to the AiDevHelpers.McpBridge MCP tools, their purposes, and safe-use notes.")]
    public static string GetToolGuide() => Load(ToolsResource);

    [McpServerResource(
        UriTemplate = "docs://ide-bridge/mcp-self-test",
        Name = "AiDevHelpers.McpBridge MCP Full Self-Test",
        MimeType = "text/markdown")]
    [Description("Full self-test checklist for the AiDevHelpers.McpBridge MCP server, automated suites, MCP resources, and optional live IDE checks.")]
    public static string GetSelfTest() => Load(SelfTestResource);
}
