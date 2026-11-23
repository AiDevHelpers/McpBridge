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
    IReadOnlyList<string> PendingProjects);

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

        return new RepoScanReport(full, findings, elevation, pending);
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

    public const string Usage = "usage:\n  elev --repo-scan <repoRoot>\n  elev repo-setup <repoRoot>";

    /// <summary>True when the first argument is a local verb; <paramref name="exit"/> then holds the exit code.</summary>
    public static bool TryRun(string[] args, TextWriter output, IConfirmer confirmer, out int exit)
    {
        exit = 0;
        if (args.Length == 0 || (args[0] != "--repo-scan" && args[0] != "repo-setup"))
            return false;

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
}
