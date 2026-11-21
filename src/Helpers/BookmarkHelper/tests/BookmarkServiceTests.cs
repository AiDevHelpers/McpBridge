using System.Text.Json;
using AiDevHelpers.Helpers.BookmarkHelper;
using Xunit;

namespace AiDevHelpers.Helpers.BookmarkHelper.Tests;

public sealed class BookmarkServiceTests
{
    private sealed class FakeBridge : IBookmarkBridge
    {
        public List<(string Action, string Json)> Calls { get; } = [];

        public Func<string, JsonElement> Reply { get; set; } = _ => JsonDocument.Parse("null").RootElement.Clone();

        public Task<JsonElement> CallAsync(string action, object args, CancellationToken ct = default)
        {
            Calls.Add((action, JsonSerializer.Serialize(args)));
            return Task.FromResult(Reply(action));
        }
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private const string Sample = "[{\"id\":\"b1\",\"file\":\"C:\\\\src\\\\App\\\\Program.cs\",\"line\":12,\"name\":\"review\"}]";

    [Fact]
    public async Task ListParsesBridgeArray()
    {
        var bridge = new FakeBridge { Reply = _ => Json(Sample) };
        var service = new BookmarkService(bridge);

        var items = await service.ListAsync();

        var b = Assert.Single(items);
        Assert.Equal("b1", b.Id);
        Assert.Equal(12, b.Line);
        Assert.Equal("review", b.Name);
        Assert.Equal("bookmarkList", bridge.Calls[0].Action);
    }

    [Fact]
    public async Task GetReturnsNullWhenBridgeResultIsNull()
    {
        var bridge = new FakeBridge { Reply = _ => Json("null") };
        var service = new BookmarkService(bridge);

        Assert.Null(await service.GetAsync("missing"));
    }

    [Fact]
    public async Task SetRefusesRelativePathWithoutCallingBridge()
    {
        var bridge = new FakeBridge();
        var service = new BookmarkService(bridge);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetAsync("Program.cs", 3, null));

        Assert.Empty(bridge.Calls);
    }

    [Fact]
    public async Task SetRefusesLineBelowOne()
    {
        var service = new BookmarkService(new FakeBridge());

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetAsync(@"C:\src\a.cs", 0, null));
    }

    [Fact]
    public async Task SetSendsFileLineAndNameAndReturnsBookmark()
    {
        const string created = "{\"id\":\"b2\",\"file\":\"C:\\\\src\\\\a.cs\",\"line\":3,\"name\":\"check\"}";
        var bridge = new FakeBridge { Reply = _ => Json(created) };
        var service = new BookmarkService(bridge);

        var b = await service.SetAsync(@"C:\src\a.cs", 3, "check");

        Assert.Equal("b2", b.Id);
        Assert.Equal("bookmarkSet", bridge.Calls[0].Action);
        Assert.Contains("\"line\":3", bridge.Calls[0].Json);
        Assert.Contains("check", bridge.Calls[0].Json);
    }

    [Fact]
    public async Task RemoveReturnsBridgeBoolean()
    {
        var bridge = new FakeBridge { Reply = _ => Json("true") };
        var service = new BookmarkService(bridge);

        Assert.True(await service.RemoveAsync("b1"));
        Assert.Equal("bookmarkRemove", bridge.Calls[0].Action);
    }

    [Fact]
    public async Task EmptyIdIsRefused()
    {
        var service = new BookmarkService(new FakeBridge());

        await Assert.ThrowsAsync<ArgumentException>(() => service.RemoveAsync(" "));
    }

    [Fact]
    public async Task BridgeFailureSurfacesAsBookmarkException()
    {
        var bridge = new FakeBridge { Reply = _ => throw new BookmarkException("VS refused") };
        var service = new BookmarkService(bridge);

        var ex = await Assert.ThrowsAsync<BookmarkException>(() => service.ListAsync());
        Assert.Equal("VS refused", ex.Message);
    }
}
