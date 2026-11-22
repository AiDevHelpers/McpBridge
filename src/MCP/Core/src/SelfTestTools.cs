using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Lists the helpers with their versions and reads each helper's embedded self-test markdown (Q4: full self test).</summary>
[McpServerToolType]
internal sealed class SelfTestTools
{
    /// <summary>Helper names without the AiDevHelpers.Helpers. prefix. Each has an embedded AiDevHelpers.SelfTest.md.</summary>
    public static readonly string[] HelperNames =
    [
        "BookmarkHelper", "ConfigHelper", "ErrorHelper", "ExtensionsHelper", "LintHelper", "LogHelper",
        "OutputHelper", "PackageHelper", "GateHelper", "SourceControlHelper", "TerminalControl", "TestingHelper", "WindowsSession",
    ];

    private const string ResourceName = "AiDevHelpers.SelfTest.md";

    [McpServerTool]
    [Description("List the helpers with their versions, the MCP server (Core) version, and whether each helper has a self-test.")]
    public static string SelfTestList()
    {
        var helpers = HelperNames.Select(name =>
        {
            var assembly = Assembly.Load("AiDevHelpers.Helpers." + name);
            return new
            {
                name,
                version = assembly.GetName().Version?.ToString(),
                has_self_test = ReadSelfTest(name) is not null,
            };
        });

        return JsonSerializer.Serialize(new
        {
            ok = true,
            core_version = typeof(SelfTestTools).Assembly.GetName().Version?.ToString(),
            helpers,
        });
    }

    [McpServerTool]
    [Description("Read a helper's self-test markdown, which is embedded in its library.")]
    public static string SelfTestRead(
        [Description("Helper name, e.g. LintHelper. See self_test_list.")] string helper)
    {
        if (!HelperNames.Contains(helper, StringComparer.OrdinalIgnoreCase))
        {
            return JsonSerializer.Serialize(new { ok = false, error = Strings.Format("UnknownHelper", helper, string.Join(", ", HelperNames)) });
        }

        var name = HelperNames.First(n => string.Equals(n, helper, StringComparison.OrdinalIgnoreCase));
        var markdown = ReadSelfTest(name);
        return markdown is null
            ? JsonSerializer.Serialize(new { ok = false, error = Strings.Format("HelperHasNoSelfTest", name) })
            : JsonSerializer.Serialize(new { ok = true, helper = name, markdown });
    }

    /// <summary>The embedded self-test markdown for a helper, or null when it has none.</summary>
    internal static string? ReadSelfTest(string helper)
    {
        var assembly = Assembly.Load("AiDevHelpers.Helpers." + helper);
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
