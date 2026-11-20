using System.Text.Json;

namespace AiDevHelpers.Helpers.BookmarkHelper;

/// <summary>A Visual Studio bookmark, as the IDE bridge reports it.</summary>
public sealed record Bookmark(string Id, string File, int Line, string? Name)
{
    internal static Bookmark FromJson(JsonElement e) => new(
        Id: e.GetProperty("id").GetString() ?? string.Empty,
        File: e.GetProperty("file").GetString() ?? string.Empty,
        Line: e.GetProperty("line").GetInt32(),
        Name: e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null);
}

/// <summary>Raised when the IDE refuses a bookmark action or the bridge returns an error.</summary>
public sealed class BookmarkException(string message) : Exception(message);

/// <summary>Calls one bridge action. Implementations throw BookmarkException on an error reply.</summary>
public interface IBookmarkBridge
{
    Task<JsonElement> CallAsync(string action, object args, CancellationToken ct = default);
}

/// <summary>
/// Get, list, set and remove Visual Studio bookmarks through the IDE bridge (D5, D17). Inputs are checked here
/// before any bridge call. VS Code is not supported in v1 (D8), so its bridge replies with unsupported.
/// </summary>
public sealed class BookmarkService(IBookmarkBridge bridge)
{
    private const int MaxNameLength = 200;

    public async Task<IReadOnlyList<Bookmark>> ListAsync(CancellationToken ct = default)
    {
        var result = await bridge.CallAsync("bookmarkList", new { }, ct);
        return result.EnumerateArray().Select(Bookmark.FromJson).ToList();
    }

    public async Task<Bookmark?> GetAsync(string id, CancellationToken ct = default)
    {
        RequireId(id);
        var result = await bridge.CallAsync("bookmarkGet", new { id }, ct);
        return result.ValueKind == JsonValueKind.Null ? null : Bookmark.FromJson(result);
    }

    public async Task<Bookmark> SetAsync(string file, int line, string? name, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(file))
        {
            throw new ArgumentException("file must be a full path.", nameof(file));
        }

        if (line < 1)
        {
            throw new ArgumentException("line must be 1 or greater.", nameof(line));
        }

        if (name is { Length: > MaxNameLength })
        {
            throw new ArgumentException($"name must be {MaxNameLength} characters or fewer.", nameof(name));
        }

        var result = await bridge.CallAsync("bookmarkSet", new { file, line, name }, ct);
        return Bookmark.FromJson(result);
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken ct = default)
    {
        RequireId(id);
        var result = await bridge.CallAsync("bookmarkRemove", new { id }, ct);
        return result.ValueKind == JsonValueKind.True;
    }

    private static void RequireId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("id is required.", nameof(id));
        }
    }
}
