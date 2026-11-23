using System.Text.Json;
using AiDevHelpers.Common;
using AiDevHelpers.Common.Contracts;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// What a scan of one repo found: the elevation findings, what the repo and this user already say, and the projects that are
/// neither listed nor declined yet (the ones setup still has to ask about).
/// </summary>
public sealed record RepoScanReport(
    string Root,
    IReadOnlyList<ElevationFinding> Findings,
    RepoElevation Elevation,
    IReadOnlyList<string> PendingProjects,
    RulesReport Rules,
    RepoRules RecordedRules,
    IReadOnlyList<string> PendingProtectedBranches,
    IReadOnlyList<string> PendingRequiredChecks);

/// <summary>
/// The repo setup helper for the unelevated <c>elev</c> client. <see cref="Scan"/> only reads. <see cref="Setup"/> creates the
/// <c>.aidevtools</c> files and asks the person, from the console, about each project; nothing is listed or changed without a yes,
/// and the questions never come from arguments, so a caller cannot answer for the person.
/// </summary>
public static class RepoSetup
{
    public static RepoScanReport Scan(string root)
    {
        var full = Path.GetFullPath(root);
        var findings = ElevationScanner.Scan(full);
        var elevation = ElevationFiles.Read(full);

        var known = elevation.Projects.Select(p => p.Path).Concat(elevation.Declined).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = findings
            .Where(f => f.Project is not null)
            .Select(f => ElevationFiles.Normalize(f.Project!))
            .Where(p => !known.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();

        var rules = RulesScanner.Scan(full);
        var recorded = RepoRulesFile.Read(full);
        var declinedRules = DeclinedRules(full);
        var pendingBranches = rules.CandidateProtectedBranches
            .Where(b => !recorded.ProtectedBranches.Contains(b, StringComparer.OrdinalIgnoreCase) && !declinedRules.Contains("branch:" + b))
            .ToList();
        var pendingChecks = rules.CandidateRequiredChecks
            .Where(c => !recorded.RequiredChecks.Contains(c, StringComparer.OrdinalIgnoreCase) && !declinedRules.Contains("check:" + c))
            .ToList();

        return new RepoScanReport(full, findings, elevation, pending, rules, recorded, pendingBranches, pendingChecks);
    }

    // The rule candidates this user said no to ("branch:main", "check:build"), kept in the gitignored user file.
    private static HashSet<string> DeclinedRules(string root)
    {
        try
        {
            var path = RepoSettingsFile.UserFilePath(root);
            var node = File.Exists(path) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) : null;
            return node?["rulesDeclined"] is System.Text.Json.Nodes.JsonArray array
                ? array.OfType<System.Text.Json.Nodes.JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void DeclineRules(string root, IEnumerable<string> keys) =>
        RepoSettingsFile.UpdateUserFile(root, node =>
        {
            var array = node["rulesDeclined"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
            node["rulesDeclined"] = array;
            foreach (var key in keys)
                array.Add(key);
        });

    // Shows what the repo's own files say, then asks before recording each kind of candidate rule.
    private static void SetupRules(RepoScanReport report, TextWriter output, IConfirmer confirmer)
    {
        var rules = report.Rules;
        foreach (var file in rules.Instructions)
        {
            output.WriteLine($"  instructions  {file.Path}");
            foreach (var line in file.Lines)
                output.WriteLine($"      {line}");
        }

        foreach (var workflow in rules.Workflows)
            output.WriteLine($"  ci  {workflow.Path}: branches [{string.Join(", ", workflow.Branches)}], jobs [{string.Join(", ", workflow.Jobs)}]");

        foreach (var hook in rules.Hooks)
            output.WriteLine($"  hook  {hook}");

        if (rules.CodeOwners is not null)
            output.WriteLine($"  codeowners  {rules.CodeOwners}");

        if (rules.Instructions.Count > 0)
            output.WriteLine("The instruction files above are for you to read; only the CI branches and checks below can become enforced rules.");

        var recorded = report.RecordedRules;
        var changed = false;
        if (report.PendingProtectedBranches.Count > 0)
        {
            var list = string.Join(", ", report.PendingProtectedBranches);
            if (confirmer.Confirm($"Record these as protected branches in repo-rules.json: {list}? The git tools will then refuse commits and pushes on them."))
            {
                recorded = recorded with
                {
                    ProtectedBranches = [.. recorded.ProtectedBranches, .. report.PendingProtectedBranches],
                    DefaultBranch = recorded.DefaultBranch ?? rules.DefaultBranch,
                };
                changed = true;
            }
            else
            {
                DeclineRules(report.Root, report.PendingProtectedBranches.Select(b => "branch:" + b));
            }
        }

        if (report.PendingRequiredChecks.Count > 0)
        {
            var list = string.Join(", ", report.PendingRequiredChecks);
            if (confirmer.Confirm($"Record these as required checks in repo-rules.json: {list}? pr_checks will then report any that a pull request lacks."))
            {
                recorded = recorded with { RequiredChecks = [.. recorded.RequiredChecks, .. report.PendingRequiredChecks] };
                changed = true;
            }
            else
            {
                DeclineRules(report.Root, report.PendingRequiredChecks.Select(c => "check:" + c));
            }
        }

        if (changed)
        {
            RepoRulesFile.Write(report.Root, recorded);
            output.WriteLine($"wrote {RepoRulesFile.FileName}");
        }
    }

    /// <summary>Exit codes: 0 done, 1 refused, 2 bad usage.</summary>
    public static int Setup(string root, TextWriter output, IConfirmer confirmer)
    {
        if (!Directory.Exists(root))
        {
            output.WriteLine($"refused: '{root}' is not a folder");
            return 1;
        }

        var report = Scan(root);
        foreach (var warning in report.Elevation.Warnings)
            output.WriteLine($"warning: {warning}");

        // The folder and the gitignore line are created here, on the person's request, never while only scanning.
        Directory.CreateDirectory(Path.Combine(report.Root, RepoSettingsFile.FolderName));
        RepoSettingsFile.EnsureUserFileGitIgnored(report.Root);
        if (!File.Exists(ElevationFiles.FilePath(report.Root)))
            File.WriteAllText(ElevationFiles.FilePath(report.Root), "{\n  \"projects\": []\n}\n");

        foreach (var loose in report.Findings.Where(f => f.Project is null))
            output.WriteLine($"  script  {loose.File}: {loose.Evidence} (no project owns it)");

        foreach (var project in report.PendingProjects)
        {
            var why = Reason(report.Findings, project);
            output.WriteLine($"  needs elevation  {project}: {why}");
            if (confirmer.Confirm($"Add '{project}' to this repo's elevated list? {why}"))
            {
                ElevationFiles.AddProject(report.Root, project, why);
                output.WriteLine($"added {project}");
            }
            else
            {
                ElevationFiles.Decline(report.Root, project);
                output.WriteLine($"declined {project}; it will not be asked again");
            }
        }

        var current = ElevationFiles.Read(report.Root);
        if (current.Projects.Count > 0 && current.Mode == ElevationMode.Listed
            && confirmer.Confirm("Always run this repo's IDE elevated, for every session? No means only the listed projects are launched elevated."))
        {
            ElevationFiles.SetMode(report.Root, ElevationMode.Always);
            output.WriteLine("mode: always");
        }

        if (report.PendingProjects.Count == 0 && report.Findings.Count == 0)
            output.WriteLine("Nothing in this repo asks for elevation.");

        SetupRules(report, output, confirmer);
        return 0;
    }

    private static string Reason(IReadOnlyList<ElevationFinding> findings, string project)
    {
        var why = string.Join("; ", findings
            .Where(f => f.Project is not null && string.Equals(ElevationFiles.Normalize(f.Project), project, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.File is null ? f.Evidence : $"{f.File}: {f.Evidence}")
            .Distinct());
        return why.Length <= 200 ? why : why[..200];
    }
}

/// <summary>
/// The <c>elev</c> verbs that run locally: they need no broker, no elevation and no ScriptHelper configuration.
/// <c>--repo-scan &lt;root&gt;</c> prints the scan as JSON; <c>repo-setup &lt;root&gt;</c> is the interactive setup.
/// </summary>
public static class ElevLocalCommands
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    public const string Usage = "usage:\n  elev --repo-scan <repoRoot>\n  elev repo-setup <repoRoot>\n  elev rules override <repoRoot> <requestId>\n  elev terminals scan\n  elev terminals setup [--all]";

    /// <summary>True when the first argument is a local verb; <paramref name="exit"/> then holds the exit code.</summary>
    public static bool TryRun(string[] args, TextWriter output, IConfirmer confirmer, out int exit, TerminalServices? terminals = null)
    {
        exit = 0;
        if (args.Length == 0 || (args[0] != "--repo-scan" && args[0] != "repo-setup" && args[0] != "rules" && args[0] != "terminals"))
            return false;

        if (args[0] == "terminals")
        {
            var services = terminals ?? TerminalServices.System();
            exit = args switch
            {
                [_, "scan"] => TerminalSetup.Scan(services, output),
                [_, "setup"] => TerminalSetup.Setup(services, askAgain: false, output, confirmer),
                [_, "setup", "--all"] => TerminalSetup.Setup(services, askAgain: true, output, confirmer),
                _ => Usage2(output),
            };
            return true;
        }

        if (args[0] == "rules")
        {
            exit = args.Length == 4 && args[1] == "override" ? OverrideRule(args[2], args[3], output, confirmer) : Usage2(output);
            return true;
        }

        if (args.Length != 2)
        {
            output.WriteLine(Usage);
            exit = 2;
            return true;
        }

        if (args[0] == "--repo-scan")
        {
            if (!Directory.Exists(args[1]))
            {
                output.WriteLine($"refused: '{args[1]}' is not a folder");
                exit = 1;
                return true;
            }

            output.WriteLine(JsonSerializer.Serialize(RepoSetup.Scan(args[1]), Json));
            return true;
        }

        exit = RepoSetup.Setup(args[1], output, confirmer);
        return true;
    }

    private static int Usage2(TextWriter output)
    {
        output.WriteLine(Usage);
        return 2;
    }

    // The person waives one refused git write. The question comes from this console, so a tool caller cannot answer it.
    private static int OverrideRule(string root, string id, TextWriter output, IConfirmer confirmer)
    {
        if (!Directory.Exists(root))
        {
            output.WriteLine($"refused: '{root}' is not a folder");
            return 1;
        }

        var full = Path.GetFullPath(root);
        if (RuleOverrides.Find(full, id, DateTime.UtcNow) is not { } request)
        {
            output.WriteLine($"refused: no pending request '{id}' for this repo (it may have expired)");
            return 1;
        }

        if (!confirmer.Confirm($"Allow ONE '{request.Action}' on '{request.Branch}' in {full}, against this repo's rules? It is valid for 10 minutes."))
        {
            output.WriteLine("not allowed; the rule still applies");
            return 1;
        }

        output.WriteLine(RuleOverrides.Grant(full, id, DateTime.UtcNow)
            ? $"allowed once: {request.Action} on {request.Branch}. Plan it again."
            : "refused: the request was already used or has expired");
        return 0;
    }
}
