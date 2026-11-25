using AiDevHelpers.Common;

namespace AiDevHelpers.Helpers.ProjectHelper;

/// <summary>
/// Folder shapes the solution's source and test projects already use. <c>{name}</c> stands for the project's own name segment.
/// A default is given only when at least two projects agree on the same shape; otherwise the caller must supply the folder.
/// </summary>
public sealed record LayoutDefaults(string? SourceFolder, string? TestFolder);

public static class SolutionLayout
{
    public static LayoutDefaults Infer(string solutionPath)
    {
        var projects = SolutionProjects.Read(solutionPath);
        var sources = new List<string>();
        var tests = new List<string>();

        foreach (var project in projects)
        {
            var file = Path.GetFileNameWithoutExtension(project);
            var dir = project.Contains('/') ? project[..project.LastIndexOf('/')] : string.Empty;
            if (dir.Length == 0)
            {
                continue;
            }

            if (file.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".Test", StringComparison.OrdinalIgnoreCase))
            {
                var key = NameSegment(file[..file.LastIndexOf('.')]);
                tests.Add(Templatise(dir, key));
            }
            else
            {
                sources.Add(Templatise(dir, NameSegment(file)));
            }
        }

        return new LayoutDefaults(Majority(sources), Majority(tests));
    }

    /// <summary>The last dotted segment of a project name: <c>AiDevHelpers.Helpers.PackageHelper</c> gives <c>PackageHelper</c>.</summary>
    private static string NameSegment(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }

    /// <summary>Replaces the segment equal to <paramref name="key"/> with <c>{name}</c>; leaves the folder alone when none matches.</summary>
    private static string Templatise(string dir, string key)
    {
        var parts = dir.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            if (string.Equals(parts[i], key, StringComparison.OrdinalIgnoreCase))
            {
                parts[i] = "{name}";
            }
        }

        return string.Join('/', parts);
    }

    private static string? Majority(IReadOnlyList<string> shapes)
    {
        if (shapes.Count == 0)
        {
            return null;
        }

        var top = shapes.GroupBy(s => s, StringComparer.Ordinal).OrderByDescending(g => g.Count()).First();
        return top.Count() >= 2 ? top.Key : null;
    }
}
