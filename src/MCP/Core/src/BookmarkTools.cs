using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Helpers.BookmarkHelper;
using AiDevHelpers.MCP.Core.Services;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Visual Studio bookmarks through the IDE bridge (D5, D8). Until the VS bookmark route is confirmed, the VS side refuses these actions.</summary>
[McpServerToolType]
internal sealed class BookmarkTools
{
    private static readonly IIdeBridge Bridge = new IdeInstanceBridge();

    [McpServerTool]
    [SafeTool]
    [Description("List Visual Studio bookmarks. Visual Studio only in v1.")]
    public static async Task<string> BookmarkList(CancellationToken ct = default)
    {
        try
        {
            var items = await Service().ListAsync(ct);
            return Json(new { ok = true, count = items.Count, items = items.Select(Project) });
        }
        catch (Exception ex) when (ex is BookmarkException or ArgumentException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [SafeTool]
    [Description("Get one Visual Studio bookmark by id.")]
    public static async Task<string> BookmarkGet([Description("Bookmark id")] string id, CancellationToken ct = default)
    {
        try
        {
            var item = await Service().GetAsync(id, ct);
            return item is null
                ? Json(new { ok = false, error = Strings.Format("BookmarkNotFound", id) })
                : Json(new { ok = true, item = Project(item) });
        }
        catch (Exception ex) when (ex is BookmarkException or ArgumentException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [SafeTool]
    [Description("Set a Visual Studio bookmark at a line. file must be a full path; line is 1-based; name is optional.")]
    public static async Task<string> BookmarkSet(
        [Description("Full path of the file")] string file,
        [Description("1-based line number")] int line,
        [Description("Optional name, up to 200 characters")] string? name = null,
        CancellationToken ct = default)
    {
        try
        {
            var item = await Service().SetAsync(file, line, name, ct);
            return Json(new { ok = true, item = Project(item) });
        }
        catch (Exception ex) when (ex is BookmarkException or ArgumentException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [SafeTool]
    [Description("Remove a Visual Studio bookmark by id.")]
    public static async Task<string> BookmarkRemove([Description("Bookmark id")] string id, CancellationToken ct = default)
    {
        try
        {
            var removed = await Service().RemoveAsync(id, ct);
            return Json(new { ok = removed, removed });
        }
        catch (Exception ex) when (ex is BookmarkException or ArgumentException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private static BookmarkService Service() => new(new IdeBookmarkBridge(Bridge));

    private static object Project(Bookmark b) => new { id = b.Id, file = b.File, line = b.Line, name = b.Name };

    private static string Json(object value) => JsonSerializer.Serialize(value);
}

/// <summary>Adapts the IDE bridge to the BookmarkHelper bridge: always Visual Studio, errors become BookmarkException.</summary>
internal sealed class IdeBookmarkBridge(IIdeBridge bridge) : IBookmarkBridge
{
    public async Task<JsonElement> CallAsync(string action, object args, CancellationToken ct = default)
    {
        var reply = await bridge.CallAsync("VisualStudio", action, args, ct);
        if (!reply.Ok)
        {
            throw new BookmarkException(reply.Error ?? Strings.Format("BookmarkRefused", action));
        }

        return reply.Result ?? JsonDocument.Parse("null").RootElement.Clone();
    }
}
