using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>A checked snippet ready to write: its target path and the .snippet XML. Nothing is written by planning.</summary>
internal sealed record SnippetPlan(bool Ok, string? Error, string? Path, string? Xml);

/// <summary>The outcome of writing a planned snippet.</summary>
internal sealed record SnippetWriteResult(bool Ok, string? Error, string? Path);

/// <summary>
/// Builds and writes a Visual Studio code snippet (.snippet) under the user's Code Snippets folder, in the language's
/// "My Code Snippets" folder. The calling AI drafts the fields; the caller confirms before a file is written.
/// </summary>
internal static class SnippetWriter
{
    private const string SnippetNamespace = "http://schemas.microsoft.com/VisualStudio/2005/CodeSnippet";

    private static readonly Regex ShortcutPattern = new("^[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant);

    /// <summary>Checks the fields and returns the target path and XML. Writes nothing.</summary>
    public static SnippetPlan Plan(string codeSnippetsRoot, string title, string shortcut, string description, string language, string code)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Fail(Strings.Get("SnippetTitleRequired"));
        if (!ShortcutPattern.IsMatch(shortcut ?? ""))
            return Fail(Strings.Get("SnippetShortcutInvalid"));
        if (string.IsNullOrWhiteSpace(code))
            return Fail(Strings.Get("SnippetCodeRequired"));
        if (!Languages.TryGetValue(language?.Trim() ?? "", out var lang))
            return Fail(Strings.Format("SnippetLanguageUnsupported", language ?? ""));

        var folder = Path.Combine(codeSnippetsRoot, lang.Folder, "My Code Snippets");
        var file = Path.Combine(folder, SafeFileName(title) + ".snippet");
        if (!Path.GetFullPath(file).StartsWith(Path.GetFullPath(codeSnippetsRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return Fail(Strings.Get("SnippetOutsideFolder"));

        var ns = XNamespace.Get(SnippetNamespace);
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "CodeSnippets",
                new XElement(ns + "CodeSnippet",
                    new XAttribute("Format", "1.0.0"),
                    new XElement(ns + "Header",
                        new XElement(ns + "Title", title.Trim()),
                        new XElement(ns + "Shortcut", shortcut),
                        new XElement(ns + "Description", description ?? ""),
                        new XElement(ns + "SnippetTypes", new XElement(ns + "SnippetType", "Expansion"))),
                    new XElement(ns + "Snippet",
                        new XElement(ns + "Code", new XAttribute("Language", lang.Id), new XCData(code))))));

        return new SnippetPlan(true, null, file, doc.Declaration + Environment.NewLine + doc.Root);
    }

    /// <summary>Writes a planned snippet. Refuses to overwrite a snippet that already exists.</summary>
    public static SnippetWriteResult Write(SnippetPlan plan)
    {
        if (!plan.Ok || plan.Path is null || plan.Xml is null)
            return new SnippetWriteResult(false, plan.Error, plan.Path);
        if (File.Exists(plan.Path))
            return new SnippetWriteResult(false, Strings.Get("SnippetExists"), plan.Path);

        Directory.CreateDirectory(Path.GetDirectoryName(plan.Path)!);
        File.WriteAllText(plan.Path, plan.Xml);
        return new SnippetWriteResult(true, null, plan.Path);
    }

    private static SnippetPlan Fail(string error) => new(false, error, null, null);

    private static string SafeFileName(string title)
    {
        var chars = title.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private static readonly Dictionary<string, (string Id, string Folder)> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CSharp"] = ("CSharp", "Visual C#"),
        ["VisualBasic"] = ("VB", "Visual Basic"),
        ["VB"] = ("VB", "Visual Basic"),
    };
}
