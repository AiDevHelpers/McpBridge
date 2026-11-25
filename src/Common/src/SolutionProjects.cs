using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AiDevHelpers.Common;

/// <summary>Reads the project files a Visual Studio solution lists: the <c>.sln</c> and the newer <c>.slnx</c> forms.</summary>
public static partial class SolutionProjects
{
    /// <summary>
    /// The project paths a solution file lists, relative to the solution's folder and written with forward slashes. A file that cannot be
    /// read, or is not valid, lists nothing.
    /// </summary>
    public static IReadOnlyList<string> Read(string solutionPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(solutionPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        IEnumerable<string> paths;
        if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                paths = XDocument.Parse(text).Descendants()
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
        else
        {
            paths = text.Split('\n')
                .Select(line => SlnProject().Match(line.Trim()))
                .Where(m => m.Success)
                .Select(m => m.Groups[1].Value)
                .ToList();
        }

        return [.. paths.Select(p => p.Replace('\\', '/')).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    [GeneratedRegex(@"^Project\(""[^""]*""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+\.\w+proj)""", RegexOptions.CultureInvariant)]
    private static partial Regex SlnProject();
}
