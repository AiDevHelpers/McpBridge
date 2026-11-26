using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AiDevHelpers.Helpers.LocalizationHelper;

/// <summary>One data entry of a resx file.</summary>
public sealed record ResxEntry(string Name, string Value, string? Comment);

/// <summary>
/// A resx file edited as text, so a load and save with no edit is byte-identical and an edit changes only its own lines.
/// Keeps the file's newline style and BOM.
/// </summary>
public sealed partial class ResxFile
{
    private string _text;
    private readonly bool _bom;

    private ResxFile(string path, string text, bool bom)
    {
        Path = path;
        _text = text;
        _bom = bom;
    }

    public string Path { get; }

    public string Newline => _text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    public IReadOnlyList<ResxEntry> Entries =>
        XDocument.Parse(_text).Root!.Elements("data")
            .Select(d => new ResxEntry(
                (string?)d.Attribute("name") ?? string.Empty,
                (string?)d.Element("value") ?? string.Empty,
                (string?)d.Element("comment")))
            .ToList();

    public static ResxFile Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return new ResxFile(path, new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
    }

    /// <summary>A new file at <paramref name="path"/> with the neutral file's header and no data entries.</summary>
    public static ResxFile CreateFrom(ResxFile neutral, string path) =>
        new(path, DataElement().Replace(neutral._text, string.Empty), neutral._bom);

    public void Add(string name, string value, string? comment)
    {
        var close = _text.LastIndexOf("</root>", StringComparison.Ordinal);
        var newline = Newline;
        var lineStart = close < 0 ? _text.Length : _text.LastIndexOf('\n', Math.Max(close - 1, 0)) + 1;
        var line = Indent() + Element(name, value, comment) + newline;
        _text = lineStart > 0 && close >= lineStart && _text[lineStart..close].Trim().Length == 0
            ? _text.Insert(lineStart, line)
            : _text.Insert(close, newline + line);
    }

    public bool Remove(string name)
    {
        var match = Find(name);
        if (!match.Success)
        {
            return false;
        }

        _text = _text.Remove(match.Index, match.Length);
        return true;
    }

    public void SetValue(string name, string value, string? comment)
    {
        var match = Find(name);
        if (!match.Success)
        {
            Add(name, value, comment);
            return;
        }

        var tail = match.Value.EndsWith('\n') ? (match.Value.EndsWith("\r\n") ? "\r\n" : "\n") : string.Empty;
        _text = _text.Remove(match.Index, match.Length).Insert(match.Index, Indent() + Element(name, value, comment) + tail);
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var body = new UTF8Encoding(false).GetBytes(_text);
        File.WriteAllBytes(Path, _bom ? [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. body] : body);
    }

    private string Indent()
    {
        var last = DataElement().Matches(_text).LastOrDefault();
        if (last is null)
        {
            return "  ";
        }

        var start = last.Index;
        var lead = start;
        while (lead > 0 && (_text[lead - 1] == ' ' || _text[lead - 1] == '\t'))
        {
            lead--;
        }

        return _text[lead..start];
    }

    private Match Find(string name) =>
        new Regex(@"[ \t]*<data\s+name=""" + Regex.Escape(System.Security.SecurityElement.Escape(name)) + @"""[^>]*>.*?</data>(?:\r?\n)?", RegexOptions.Singleline).Match(_text);

    private static string Element(string name, string value, string? comment) =>
        $"<data name=\"{System.Security.SecurityElement.Escape(name)}\" xml:space=\"preserve\"><value>{new XText(value)}</value>"
        + (comment is null ? string.Empty : $"<comment>{new XText(comment)}</comment>")
        + "</data>";

    [GeneratedRegex(@"[ \t]*<data\b.*?</data>(?:\r?\n)?", RegexOptions.Singleline)]
    private static partial Regex DataElement();
}
