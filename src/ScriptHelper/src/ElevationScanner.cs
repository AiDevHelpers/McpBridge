using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>Why a project or script needs administrator rights.</summary>
public enum ElevationKind
{
    /// <summary>The project's app.manifest asks for requireAdministrator.</summary>
    ProjectManifest,

    /// <summary>A script that a project's launch profile runs carries an elevation marker, so debugging that project needs elevation.</summary>
    DebugScript,

    /// <summary>A script that a VS Code configuration or task runs carries an elevation marker; no project owns it.</summary>
    ScriptMarker,
}

/// <summary>One thing in a repo that needs elevation. Project is the repo-relative project file (or folder), null when no project owns it.</summary>
public sealed record ElevationFinding(string? Project, ElevationKind Kind, string Evidence, string? File = null);

/// <summary>
/// Looks through a repo for projects and launch scripts that need administrator rights: an app.manifest asking for
/// requireAdministrator, and elevation markers in the scripts the launch and debug configurations run. Reads only, and only
/// inside the repo. The findings are suggestions for the user, never decisions.
/// </summary>
public static class ElevationScanner
{
    private const int MaxFileBytes = 256 * 1024;

    private static readonly Regex AdminLevel = new(@"requestedExecutionLevel[^>]*level\s*=\s*""requireAdministrator""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ManifestProperty = new(@"<ApplicationManifest>\s*([^<]+?)\s*</ApplicationManifest>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SlnProject = new(@"^Project\(""[^""]*""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+\.\w+proj)""", RegexOptions.CultureInvariant);

    private static readonly (string Name, Regex Pattern)[] PowerShellMarkers =
    [
        ("#Requires -RunAsAdministrator", new Regex(@"^\s*#requires\b.*-RunAsAdministrator", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("Start-Process -Verb RunAs", new Regex(@"-Verb\s+RunAs\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("IsInRole(Administrator)", new Regex(@"IsInRole\s*\(.*Administrator", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
    ];

    private static readonly (string Name, Regex Pattern)[] ShellMarkers =
    [
        ("net session", new Regex(@"\bnet\s+session\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("fltmc", new Regex(@"\bfltmc\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("-Verb RunAs", new Regex(@"-Verb\s+RunAs\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
    ];

    public static IReadOnlyList<ElevationFinding> Scan(string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var projects = Projects(fullRoot);
        var findings = new List<ElevationFinding>();

        foreach (var project in projects)
        {
            if (ManifestEvidence(fullRoot, project) is { } evidence)
                findings.Add(new ElevationFinding(project, ElevationKind.ProjectManifest, evidence));
        }

        foreach (var script in LaunchScriptScanner.Scan(fullRoot, []))
        {
            if (MarkerIn(Path.Combine(fullRoot, script.Path.Replace('/', Path.DirectorySeparatorChar))) is not { } marker)
                continue;

            var owners = script.Owners ?? [];
            foreach (var folder in owners.Where(o => o.Project is not null).Select(o => o.Project!).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new ElevationFinding(ProjectFor(projects, folder), ElevationKind.DebugScript, marker, script.Path));
            }

            if (owners.Count == 0 || owners.Any(o => o.Project is null))
                findings.Add(new ElevationFinding(null, ElevationKind.ScriptMarker, marker, script.Path));
        }

        return findings
            .OrderBy(f => f.Project, StringComparer.Ordinal)
            .ThenBy(f => f.Kind)
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .ToList();
    }

    // The project file in a launchSettings owner's folder, else the folder itself.
    private static string ProjectFor(IReadOnlyList<string> projects, string folder) =>
        projects.FirstOrDefault(p => string.Equals(Path.GetDirectoryName(p)?.Replace('\\', '/') ?? string.Empty, folder == "." ? string.Empty : folder, StringComparison.OrdinalIgnoreCase))
        ?? folder;

    // Repo-relative project files: the root solution's projects, or every project file when there is no usable solution.
    private static List<string> Projects(string root)
    {
        var listed = new List<string>();
        foreach (var solution in Directory.EnumerateFiles(root).Where(f => f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)))
        {
            listed.AddRange(SolutionProjects(solution));
        }

        var projects = listed
            .Select(p => Inside(root, Path.Combine(root, p.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar))))
            .OfType<string>()
            .Where(p => File.Exists(Path.Combine(root, p.Replace('/', Path.DirectorySeparatorChar))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (projects.Count > 0)
            return projects;

        return RepoWalk.Files(root)
            .Where(f => f.File.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(f => Inside(root, f.File))
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<string> SolutionProjects(string solution)
    {
        string text;
        try
        {
            text = File.ReadAllText(solution);
        }
        catch (IOException)
        {
            return [];
        }

        if (solution.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return XDocument.Parse(text).Descendants()
                    .Where(e => e.Name.LocalName == "Project")
                    .Select(e => e.Attribute("Path")?.Value)
                    .OfType<string>()
                    .ToList();
            }
            catch (System.Xml.XmlException)
            {
                return [];
            }
        }

        return text.Split('\n')
            .Select(line => SlnProject.Match(line.Trim()))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

    // The repo-relative path of a file that is inside the repo, or null.
    private static string? Inside(string root, string full)
    {
        var rel = Path.GetRelativePath(root, Path.GetFullPath(full));
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? null : rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string? ManifestEvidence(string root, string project)
    {
        var projectFile = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
        var projectDir = Path.GetDirectoryName(projectFile)!;
        var candidates = new List<string>();

        var projectText = ReadText(projectFile);
        if (projectText is not null && ManifestProperty.Match(projectText) is { Success: true } named)
            candidates.Add(Path.Combine(projectDir, named.Groups[1].Value.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar)));

        candidates.Add(Path.Combine(projectDir, "app.manifest"));
        candidates.Add(Path.Combine(projectDir, "Properties", "app.manifest"));

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (Inside(root, full) is not { } rel || ReadText(full) is not { } text)
                continue;

            if (AdminLevel.IsMatch(text))
                return $"{rel}: requestedExecutionLevel requireAdministrator";
        }

        return null;
    }

    // The first elevation marker in a script, as text for the user, or null. Comment lines never count.
    private static string? MarkerIn(string path)
    {
        var text = ReadText(path);
        if (text is null)
            return null;

        var extension = Path.GetExtension(path).ToLowerInvariant();
        var markers = extension == ".ps1" ? PowerShellMarkers : ShellMarkers;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.TrimStart();
            foreach (var (name, pattern) in markers)
            {
                var isRequires = extension == ".ps1" && name.StartsWith("#Requires", StringComparison.Ordinal);
                if (!isRequires && IsComment(trimmed, extension))
                    continue;

                if (pattern.IsMatch(line))
                    return $"{name} at line {i + 1}";
            }
        }

        return null;
    }

    private static bool IsComment(string trimmedLine, string extension) => extension switch
    {
        ".cmd" or ".bat" => trimmedLine.StartsWith("::", StringComparison.Ordinal) || trimmedLine.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) || trimmedLine.Equals("rem", StringComparison.OrdinalIgnoreCase),
        _ => trimmedLine.StartsWith('#'),
    };

    private static string? ReadText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxFileBytes ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
