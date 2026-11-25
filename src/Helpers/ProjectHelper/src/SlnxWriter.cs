using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AiDevHelpers.Helpers.ProjectHelper;

/// <summary>
/// Adds a <c>Project</c> entry to an <c>.slnx</c> file as text. Only the new line changes; the rest of the bytes, the line endings
/// and the byte order mark are kept.
/// </summary>
public static class SlnxWriter
{
    /// <summary>Returns the file with <paramref name="projectPath"/> listed in <paramref name="solutionFolder"/> (created when missing), or at the top level when the folder is null.</summary>
    public static byte[] Insert(byte[] original, string? solutionFolder, string projectPath)
    {
        var hasBom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(original, hasBom ? 3 : 0, original.Length - (hasBom ? 3 : 0));
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new XmlException(Strings.Get("FixNoRootElement"));
        if (root.Descendants().Any(e => e.Name.LocalName == "Project" && string.Equals(e.Attribute("Path")?.Value, projectPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(Strings.Format("SlnxAlreadyListed", projectPath));
        }

        var parent = root;
        if (solutionFolder is not null)
        {
            parent = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Folder" && e.Attribute("Name")?.Value == solutionFolder)
                ?? ProjectFixer.AppendChild(root, new XElement(root.Name.Namespace + "Folder", new XAttribute("Name", solutionFolder)), newline);
        }

        ProjectFixer.AppendChild(parent, new XElement(root.Name.Namespace + "Project", new XAttribute("Path", projectPath)), newline);

        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = doc.Declaration is null, Indent = false };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            doc.Save(writer);
        }

        var output = new UTF8Encoding(false).GetString(stream.ToArray()).Replace("\r\n", "\n").Replace("\n", newline);
        var bytes = new UTF8Encoding(false).GetBytes(output);
        return hasBom ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes;
    }
}
