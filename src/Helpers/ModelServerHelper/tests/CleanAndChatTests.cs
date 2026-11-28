using System.Net;
using System.Text.Json;
using Xunit;

namespace AiDevHelpers.Helpers.ModelServerHelper.Tests;

public sealed class CleanAndChatTests
{
    private const string Url = "http://localhost:11435";

    private static readonly Func<TimeSpan, CancellationToken, Task> NoWait = (_, _) => Task.CompletedTask;

    private static ModelServerGenerator Make(FakeHandler h) =>
        new(new HttpClient(h), new Uri(Url), NoWait);

    private static FakeHandler Replies(Action<JsonDocument>? seen = null) =>
        new(async r =>
        {
            var body = await r.Content!.ReadAsStringAsync();
            seen?.Invoke(JsonDocument.Parse(body));
            return FakeHandler.Json("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}");
        });

    [Fact]
    public void Cleanup_trims_and_removes_control_characters_but_keeps_newlines_and_tabs()
    {
        var cleaned = PromptCleaner.Clean("  \u0007hello\tworld\r\nline2\u0000  ");

        Assert.Equal("hello\tworld\r\nline2", cleaned);
    }

    [Fact]
    public async Task Clean_and_chat_sends_the_history_then_the_cleaned_prompt()
    {
        JsonDocument? sent = null;
        var h = Replies(d => sent = d);
        var history = new[] { new ChatTurn("system", "be brief"), new ChatTurn("assistant", "earlier reply") };

        var result = await Make(h).CleanAndChatAsync("m", "  hi\u0007 there ", history, 120, CancellationToken.None);

        Assert.True(result.Reply.Ok);
        Assert.Equal(0, result.HistoryTrimmed);
        var messages = sent!.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("be brief", messages[0].GetProperty("content").GetString());
        Assert.Equal("earlier reply", messages[1].GetProperty("content").GetString());
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.Equal("hi there", messages[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_prompt_over_20000_characters_is_refused_before_any_request()
    {
        var h = Replies();

        var result = await Make(h).CleanAndChatAsync("m", new string('a', 20001), [], 120, CancellationToken.None);

        Assert.False(result.Reply.Ok);
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task A_history_entry_with_an_invalid_role_is_refused_before_any_request()
    {
        var h = Replies();

        var result = await Make(h).CleanAndChatAsync("m", "hi", [new ChatTurn("tool", "x")], 120, CancellationToken.None);

        Assert.False(result.Reply.Ok);
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task A_history_entry_with_empty_content_is_refused_before_any_request()
    {
        var h = Replies();

        var result = await Make(h).CleanAndChatAsync("m", "hi", [new ChatTurn("user", "   ")], 120, CancellationToken.None);

        Assert.False(result.Reply.Ok);
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task Only_the_most_recent_20_history_entries_are_sent_and_the_rest_are_counted()
    {
        JsonDocument? sent = null;
        var h = Replies(d => sent = d);
        var history = Enumerable.Range(1, 25).Select(i => new ChatTurn("user", $"turn {i}")).ToArray();

        var result = await Make(h).CleanAndChatAsync("m", "now", history, 120, CancellationToken.None);

        Assert.True(result.Reply.Ok);
        Assert.Equal(5, result.HistoryTrimmed);
        var messages = sent!.RootElement.GetProperty("messages");
        Assert.Equal(21, messages.GetArrayLength());
        Assert.Equal("turn 6", messages[0].GetProperty("content").GetString());
    }
}
