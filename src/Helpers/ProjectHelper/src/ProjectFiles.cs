using System.Xml.Linq;
using AiDevHelpers.Helpers.PackageHelper;

namespace AiDevHelpers.Helpers.ProjectHelper;

/// <summary>
/// One project file as written. Properties are read from the project file only; values inherited from a Directory.Build.props
/// are not seen, so a property set there is reported as missing.
/// </summary>
public sealed record ProjectFile(
    string Path,
    string Name,
    IReadOnlyList<string> Frameworks,
    IReadOnlyDictionary<string, string> Properties,
    IReadOnlyList<PackageUse> Uses);

public static class ProjectFiles
{
    /// <summary>Every .csproj under the root, outside bin and obj, with paths relative to the root and forward slashes.</summary>
    public static IReadOnlyList<ProjectFile> Read(string root)
    {
        var uses = PackageVersions.ReadAll(root);
        var projects = new List<ProjectFile>();
        foreach (var file in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         // Test fixtures are sample inputs, not repo projects.
                         && !p.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}Fixtures{Path.DirectorySeparatorChar}")))
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(file);
            }
            catch (System.Xml.XmlException)
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var frameworks = doc.Descendants("TargetFramework").Select(e => e.Value.Trim())
                .Concat(doc.Descendants("TargetFrameworks").SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var element in doc.Descendants().Where(e => e.Parent?.Name.LocalName == "PropertyGroup"))
            {
                var value = element.Value.Trim();
                if (value.Length > 0 && !properties.ContainsKey(element.Name.LocalName))
                {
                    properties[element.Name.LocalName] = value;
                }
            }

            projects.Add(new ProjectFile(
                relative,
                Path.GetFileNameWithoutExtension(file),
                frameworks,
                properties,
                uses.Where(u => u.Project == relative).ToList()));
        }

        return projects;
    }
}
