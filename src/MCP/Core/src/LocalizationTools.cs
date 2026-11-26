using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Helpers.LocalizationHelper;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Localization checks: hardcoded user-facing strings and the Strings.resx keys against the code that uses them.</summary>
[McpServerToolType]
internal sealed class LocalizationTools
{
    [McpServerTool]
    [Description("List multi-word string literals in C# source that should be in Resources/Strings.resx. Takes a .cs file or a folder. " +
                 "Comments, attributes, logger calls, Strings.cs and bin or obj folders are skipped. resxKey names the resource when the text is already in one.")]
    public static string StringScan(
        [Description("A .cs file or a folder")] string path)
    {
        try
        {
            var findings = StringScanner.Scan(path);
            return Json(new { ok = true, count = findings.Count, findings });
        }
        catch (DirectoryNotFoundException ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [Description("Check each Resources/Strings.resx under a folder against the Strings.Get and Strings.Format calls beside it: " +
                 "duplicate names, keys used in code but missing from the resx, and keys never used.")]
    public static string ResxCheck(
        [Description("Folder to check, usually the repo root")] string root)
    {
        try
        {
            var findings = ResxChecker.Check(root);
            return Json(new { ok = true, count = findings.Count, findings });
        }
        catch (DirectoryNotFoundException ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
