using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Helpers.LocalizationHelper;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Localization checks: hardcoded user-facing strings and the Strings.resx keys against the code that uses them.</summary>
[McpServerToolType]
internal sealed class LocalizationTools
{
    [McpServerTool]
    [SafeTool]
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
    [SafeTool]
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
    [SafeTool]
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
    [SafeTool]
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
    [SafeTool]
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
    [SafeTool]
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

    [McpServerTool]
    [SafeTool]
    [Description("Write a pseudo-localized copy (Strings.qps-ploc.resx) of a neutral Strings.resx: letters become accented look-alikes, text grows about 30 percent and is wrapped in brackets, " +
                 "placeholders such as {0} are kept. Run the app with that culture to find unlocalized text and layout limits. The file is generated, so do not commit it. " +
                 "Two steps: without confirm it returns the first 20 changes and a token and writes nothing; call again with confirm set to the token to write.")]
    public static string LocalizationPseudo(
        [Description("Folder the output must stay inside, usually the repo root")] string root,
        [Description("The neutral Strings.resx, absolute or relative to root")] string resx,
        [Description("Output path inside root, ending in .resx; default is Strings.qps-ploc.resx beside the neutral file")] string? output = null,
        [Description("The token from the plan call; writes that plan")] string? confirm = null)
    {
        try
        {
            var writer = new PseudoWriter(PseudoPlans);
            if (confirm is null)
            {
                var plan = writer.Plan(root, resx, output);
                return Json(new
                {
                    ok = true,
                    plan = true,
                    token = plan.Token,
                    expires_utc = plan.ExpiresUtc,
                    count = plan.Changes.Count,
                    output = Path.GetRelativePath(plan.Root, plan.OutputPath),
                    changes = plan.Changes.Take(PseudoPreviewLimit).Select(c => new { key = c.Key, source = c.Source, pseudo = c.Pseudo }),
                });
            }

            var file = writer.Apply(confirm);
            return Json(new { ok = true, applied = true, count = 1, files = new[] { file } });
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException or ArgumentException or InvalidOperationException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [SafeTool]
    [Description("Translate the stubs (comment needs-translation) of one culture's Strings.<culture>.resx with the connected client's model, through MCP sampling. " +
                 "The client must support sampling; otherwise confirm is refused. Two steps: without confirm it lists the keys, their neutral text, the batch count and the prompt, sends nothing and writes nothing; " +
                 "call again with confirm set to the token to translate. Each reply is checked: placeholders such as {0} must match, the text must not be empty, and a line break is refused unless the source has one. " +
                 "Valid translations are written with the comment machine-translated for human review; rejected keys stay stubs and come back with a reason.")]
    public static async Task<string> LocalizationTranslate(
        McpServer server,
        [Description("Folder to work in, usually the repo root")] string root,
        [Description("The culture to translate into, for example fr")] string culture,
        [Description("Only these keys; each must be a stub. Omit for every stub")] string[]? keys = null,
        [Description("The token from the plan call; sends the batches and writes the valid translations")] string? confirm = null,
        CancellationToken ct = default) =>
        await Translate(server.ClientCapabilities, () => new SamplingTranslator(server), root, culture, keys, confirm, ct);

    internal static async Task<string> Translate(ClientCapabilities? capabilities, Func<ITranslator> translator, string root, string culture, string[]? keys, string? confirm, CancellationToken ct)
    {
        var sampling = SamplingTranslator.IsAvailable(capabilities);
        try
        {
            var service = new TranslationService(new LazyTranslator(translator), TranslatePlans);
            if (confirm is null)
            {
                var plan = service.Plan(root, culture, keys);
                return Json(new
                {
                    ok = true,
                    plan = true,
                    token = plan.Token,
                    expires_utc = plan.ExpiresUtc,
                    count = plan.Items.Count,
                    batches = plan.Batches,
                    sampling_available = sampling,
                    prompt = plan.SystemPrompt,
                    items = plan.Items.Select(i => new { file = i.Resx, key = i.Key, source = i.Source }),
                });
            }

            if (!sampling)
            {
                return Json(new { ok = false, error = Strings.Get("SamplingNotSupported") });
            }

            var result = await service.ApplyAsync(confirm, ct);
            return Json(new
            {
                ok = true,
                applied = true,
                count = result.Written.Count,
                files = result.Files,
                rejected = result.Rejected.Select(r => new { key = r.Key, reason = r.Reason }),
            });
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException or InvalidOperationException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private sealed class LazyTranslator(Func<ITranslator> create) : ITranslator
    {
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct) =>
            create().CompleteAsync(systemPrompt, userPrompt, ct);
    }

    private static readonly PlanStore<TranslatePlan> TranslatePlans = new();

    private const int PseudoPreviewLimit = 20;

    private static readonly PlanStore<PseudoPlan> PseudoPlans = new();

    private static readonly PlanStore<ExtractPlan> ExtractPlans = new();

    private static readonly PlanStore<SyncPlan> SyncPlans = new();

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
