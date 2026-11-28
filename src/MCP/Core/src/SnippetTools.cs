using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>
/// Lists the Visual Studio code snippets the user has: the <c>Code Snippets</c> folders under Documents\Visual Studio *.
/// Read-only; the snippet text is not returned, only its header, so the list stays small.
/// </summary>
[McpServerToolType]
internal sealed class SnippetTools
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [McpServerTool]
    [SafeTool]
    [Description("List Visual Studio code snippets from the user's Code Snippets folders: title, shortcut, description, language, snippet types and file path. filter matches title, shortcut or description; language matches the snippet's language (for example CSharp). Read-only.")]
    public static string IdeListSnippets(
        [Description("Optional: text to find in the title, shortcut or description, ignoring case")] string? filter = null,
        [Description("Optional: language, such as CSharp or VisualBasic")] string? language = null,
        [Description("Optional: most snippets to return (default 200, at most 1000)")] int? limit = null)
    {
        try
        {
            return ListSnippetsIn(UserSnippetFolders(), filter, language, limit ?? DefaultLimit);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message }, JsonOptions);
        }
    }

    /// <summary>Scans the given folders and returns the JSON reply. Used by the tool and by tests.</summary>
    internal static string ListSnippetsIn(IEnumerable<string> roots, string? filter, string? language, int limit)
    {
        var max = Math.Min(Math.Max(limit, 1), MaxLimit);
        var scan = SnippetCatalog.Scan(roots, filter, language, max);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            count = scan.Snippets.Count,
            truncated = scan.Truncated,
            snippets = scan.Snippets.Select(s => new { title = s.Title, shortcut = s.Shortcut, description = s.Description, language = s.Language, snippetTypes = s.SnippetTypes, path = s.Path }).ToList(),
        }, JsonOptions);
    }

    // Documents\Visual Studio <version>\Code Snippets, for each installed version folder.
    private static IEnumerable<string> UserSnippetFolders()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents) || !Directory.Exists(documents))
            yield break;

        foreach (var versionFolder in Directory.EnumerateDirectories(documents, "Visual Studio *"))
        {
            var snippets = Path.Combine(versionFolder, "Code Snippets");
            if (Directory.Exists(snippets))
                yield return snippets;
        }
    }
}
