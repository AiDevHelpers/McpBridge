using System.Text;

namespace AiDevHelpers.Helpers.LocalizationHelper.Tests;

/// <summary>Builds resx files in a temp folder for tests.</summary>
internal sealed class ResxFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "loc-" + Guid.NewGuid().ToString("N"));

    public ResxFixture() => Directory.CreateDirectory(Root);

    public void Dispose() => Directory.Delete(Root, recursive: true);

    /// <summary>Writes Resources/Strings[.culture].resx under <paramref name="project"/> and returns its full path.</summary>
    public string Resx(string project, string? culture, string newline = "\n", bool bom = false, params (string Name, string Value, string? Comment)[] entries)
    {
        var name = culture is null ? "Strings.resx" : $"Strings.{culture}.resx";
        var path = Path.Combine(Root, project, "Resources", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>").Append(newline);
        sb.Append("<root>").Append(newline);
        sb.Append("  <resheader name=\"resmimetype\"><value>text/microsoft-resx</value></resheader>").Append(newline);
        foreach (var (n, v, c) in entries)
        {
            sb.Append("  <data name=\"").Append(n).Append("\" xml:space=\"preserve\"><value>").Append(v).Append("</value>");
            if (c is not null)
            {
                sb.Append("<comment>").Append(c).Append("</comment>");
            }

            sb.Append("</data>").Append(newline);
        }

        sb.Append("</root>").Append(newline);
        File.WriteAllBytes(path, new UTF8Encoding(bom).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray());
        return path;
    }

    public string Cs(string project, string file, string text)
    {
        var path = Path.Combine(Root, project, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
}
