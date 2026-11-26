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

    [McpServerTool]
    [Description("Compare each Strings.<culture>.resx under a folder with the neutral Strings.resx beside it. Findings: missing (key not in the culture file), " +
                 "extra (key not in the neutral file), untranslated (same text as neutral, or comment needs-translation; comment same-in-culture suppresses it), " +
                 "placeholder-mismatch ({0}-style tokens differ) and invalid-culture (file name is not a culture). The pseudo culture qps-ploc is skipped.")]
    public static string LocalizationSyncCheck(
        [Description("Folder to check, usually the repo root")] string root)
    {
        try
        {
            var findings = CultureSyncChecker.Check(root);
            return Json(new { ok = true, count = findings.Count, findings = findings.Select(f => new { resx = f.Resx, kind = f.Kind, key = f.Key, culture = f.Culture }) });
        }
        catch (DirectoryNotFoundException ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [Description("Bring culture resx files in line with the neutral Strings.resx: add missing keys as stubs (neutral text, comment needs-translation), " +
                 "create a new culture file, and with removeExtra remove keys the neutral file does not have. Two steps: without confirm it returns the changes and a token and writes nothing; " +
                 "call again with confirm set to that token to apply. A file changed since the plan is refused.")]
    public static string LocalizationSync(
        [Description("Folder to sync, usually the repo root")] string root,
        [Description("One culture to sync, for example fr (created if it has no file); omit for every existing culture file")] string? culture = null,
        [Description("Also remove keys that are not in the neutral file")] bool removeExtra = false,
        [Description("The token from the plan call; applies that plan")] string? confirm = null)
    {
        try
        {
            var writer = new CultureSyncWriter(SyncPlans);
            if (confirm is null)
            {
                var plan = writer.Plan(root, culture, removeExtra);
                return Json(new
                {
                    ok = true,
                    plan = true,
                    token = plan.Token,
                    expires_utc = plan.ExpiresUtc,
                    count = plan.Changes.Count,
                    changes = plan.Changes.Select(c => new { resx = c.Resx, action = c.Action, key = c.Key, culture = c.Culture }),
                });
            }

            var files = writer.Apply(confirm);
            return Json(new { ok = true, applied = true, count = files.Count, files });
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException or InvalidOperationException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private static readonly PlanStore<SyncPlan> SyncPlans = new();

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
