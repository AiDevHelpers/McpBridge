using System.Xml.Linq;

namespace AiDevHelpers.Helpers.TestingHelper;

/// <summary>
/// Finds the test projects a change can affect (plan Phase 6). A changed file belongs to the nearest project above it.
/// Every project that references that project, directly or through others, is affected, and so are its tests.
/// A build-wide file (Directory.Build.props, Directory.Packages.props, .editorconfig, a solution file) affects every test project.
/// Pure file reads; nothing is built or run.
/// </summary>
public static class AffectedTests
{
    private static readonly string[] SkipFolders = ["bin", "obj", "node_modules", "dist", ".git"];
    private static readonly string[] BuildWideFiles = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "nuget.config", ".editorconfig", "global.json"];

    /// <summary>Full paths of the test projects to run for these changed files (relative to <paramref name="root"/> or absolute), sorted.</summary>
    public static IReadOnlyList<string> Find(string root, IEnumerable<string> changedFiles)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(Strings.Format("FolderNotFound", root));
        }

        var fullRoot = Path.GetFullPath(root);
        var projects = Directory.EnumerateFiles(fullRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => SkipFolders.Contains(part, StringComparer.OrdinalIgnoreCase)))
            .Select(Path.GetFullPath)
            .ToList();

        var references = projects.ToDictionary(p => p, ReferencesOf, StringComparer.OrdinalIgnoreCase);
        var testProjects = projects.Where(IsTestProject).ToList();

        var changed = changedFiles
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => Path.GetFullPath(Path.IsPathRooted(f) ? f : Path.Combine(fullRoot, f)))
            .ToList();

        if (changed.Any(f => IsBuildWide(f)))
        {
            return Sorted(testProjects);
        }

        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in changed)
        {
            var owner = OwnerOf(file, projects);
            if (owner is not null)
            {
                affected.Add(owner);
            }
        }

        // Add every project that references an affected project, until nothing new is added.
        bool grew;
        do
        {
            grew = false;
            foreach (var (project, referenced) in references)
            {
                if (!affected.Contains(project) && referenced.Any(affected.Contains))
                {
                    affected.Add(project);
                    grew = true;
                }
            }
        }
        while (grew);

        return Sorted(testProjects.Where(affected.Contains));
    }

    private static IReadOnlyList<string> Sorted(IEnumerable<string> paths) =>
        paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    private static bool IsBuildWide(string file)
    {
        var name = Path.GetFileName(file);
        return BuildWideFiles.Contains(name, StringComparer.OrdinalIgnoreCase)
               || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
    }

    private static string? OwnerOf(string file, IEnumerable<string> projects)
    {
        string? best = null;
        var bestLength = -1;
        foreach (var project in projects)
        {
            var dir = Path.GetDirectoryName(project)! + Path.DirectorySeparatorChar;
            if (file.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && dir.Length > bestLength)
            {
                best = project;
                bestLength = dir.Length;
            }
        }

        return best;
    }

    private static IReadOnlyList<string> ReferencesOf(string project)
    {
        var dir = Path.GetDirectoryName(project)!;
        try
        {
            return XDocument.Load(project).Descendants("ProjectReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => Path.GetFullPath(Path.Combine(dir, v!.Replace('\\', Path.DirectorySeparatorChar))))
                .ToList();
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    private static bool IsTestProject(string project)
    {
        var text = File.ReadAllText(project);
        return text.Contains("<IsTestProject>true</IsTestProject>", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase);
    }
}
