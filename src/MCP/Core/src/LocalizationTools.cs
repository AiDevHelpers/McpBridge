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

    [McpServerTool]
    [Description("Move hardcoded multi-word string literals in a .cs file or folder into the project's Resources/Strings.resx and rewrite them as Strings.Get(\"Key\") " +
                 "or, for interpolated strings, Strings.Format(\"Key\", args). Two steps: without confirm it returns the items (file, line, literal, key, replacement) and the skipped literals " +
                 "with a reason code, and writes nothing; call again with confirm set to the token to apply. Skipped reasons: no-strings-class, namespace-mismatch, const-context, case-label, " +
                 "interpolation-format, verbatim-or-raw, unsupported-escape. keys maps a literal's text to the resource key you want.")]
    public static string LocalizationExtract(
        [Description("A .cs file or a folder")] string path,
        [Description("Optional keys by literal text, for example {\"Could not read the file.\": \"ReadFailed\"}")] Dictionary<string, string>? keys = null,
        [Description("The token from the plan call; applies that plan")] string? confirm = null)
    {
        try
        {
            var extractor = new StringExtractor(ExtractPlans);
            if (confirm is null)
            {
                var plan = extractor.Plan(path, keys);
                return Json(new
                {
                    ok = true,
                    plan = true,
                    token = plan.Token,
                    expires_utc = plan.ExpiresUtc,
                    count = plan.Items.Count,
                    items = plan.Items.Select(i => new { file = i.File, line = i.Line, literal = i.Literal, key = i.Key, replacement = i.Replacement }),
                    skipped = plan.Skipped.Select(s => new { file = s.File, line = s.Line, literal = s.Literal, reason = s.Reason }),
                });
            }

            var files = extractor.Apply(confirm);
            return Json(new { ok = true, applied = true, count = files.Count, files });
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException or InvalidOperationException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [Description("List culture-sensitive C# calls that name no culture or comparison: string.Format, Parse and TryParse on numbers and dates, ToUpper and ToLower, string.Compare, " +
                 "StartsWith, EndsWith, IndexOf and LastIndexOf with a string, and ToString() on numbers and dates. Takes a .cs file or a folder (bin and obj are skipped). " +
                 "Uses the compiler's semantic model, so a call whose types do not resolve is skipped. A trailing // culture-ok comment suppresses a line. Report only; nothing is changed.")]
    public static string LocalizationCultureCheck(
        [Description("A .cs file or a folder")] string path)
    {
        try
        {
            var findings = CultureCallChecker.Check(path);
            return Json(new { ok = true, count = findings.Count, findings = findings.Select(f => new { file = f.File, line = f.Line, kind = f.Kind, call = f.Call, suggestion = f.Suggestion }) });
        }
        catch (DirectoryNotFoundException ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private static readonly PlanStore<ExtractPlan> ExtractPlans = new();

    private static readonly PlanStore<SyncPlan> SyncPlans = new();

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
