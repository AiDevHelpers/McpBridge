using System.Net;
using System.Text.Json;
using Xunit;

namespace AiDevHelpers.Helpers.ModelServerHelper.Tests;

public sealed class ModelServerGeneratorTests
{
    private const string Url = "http://localhost:11435";

    private static readonly Func<TimeSpan, CancellationToken, Task> NoWait = (_, _) => Task.CompletedTask;

    private static ModelServerGenerator Make(FakeHandler h) =>
        new(new HttpClient(h), new Uri(Url), NoWait);

    private static FakeHandler Replies(string body) =>
        new(_ => Task.FromResult(FakeHandler.Json(body)));

    [Fact]
    public async Task Generate_posts_to_api_generate_and_returns_the_response()
    {
        var h = Replies("{\"model\":\"m\",\"response\":\"hi\",\"done\":true}");

        var r = await Make(h).GenerateAsync("m", "say hi", null, 120, CancellationToken.None);

        Assert.True(r.Ok);
        Assert.Equal("hi", r.Text);
        Assert.False(r.Capped);
        Assert.Equal(["/api/generate"], h.Paths);
    }

    [Fact]
    public async Task Generate_sends_stream_false_and_omits_system_when_not_given()
    {
        string? body = null;
        var h = new FakeHandler(async r =>
        {
            body = await r.Content!.ReadAsStringAsync();
            return FakeHandler.Json("{\"response\":\"x\"}");
        });

        await Make(h).GenerateAsync("m", "p", null, 120, CancellationToken.None);

        using var doc = JsonDocument.Parse(body!);
        Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("system", out _));
    }

    [Fact]
    public async Task Generate_sends_the_system_text_when_given()
    {
        string? body = null;
        var h = new FakeHandler(async r =>
        {
            body = await r.Content!.ReadAsStringAsync();
            return FakeHandler.Json("{\"response\":\"x\"}");
        });

        await Make(h).GenerateAsync("m", "p", "be brief", 120, CancellationToken.None);

        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("be brief", doc.RootElement.GetProperty("system").GetString());
    }

    [Fact]
    public async Task Chat_posts_to_v1_chat_completions_and_returns_the_content()
    {
        var h = Replies("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"yo\"}}]}");

        var r = await Make(h).ChatAsync("m", [new ChatTurn("user", "hi")], 120, CancellationToken.None);

        Assert.True(r.Ok);
        Assert.Equal("yo", r.Text);
        Assert.Equal(["/v1/chat/completions"], h.Paths);
    }

    [Fact]
    public async Task Chat_refuses_an_invalid_role_before_any_request()
    {
        var h = Replies("{}");

        var r = await Make(h).ChatAsync("m", [new ChatTurn("tool", "x")], 120, CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Empty(h.Paths);
    }

    [Theory]
    [InlineData("", "p")]
    [InlineData("m", "")]
    public async Task Generate_refuses_an_empty_model_or_prompt_before_any_request(string model, string prompt)
    {
        var h = Replies("{}");

        var r = await Make(h).GenerateAsync(model, prompt, null, 120, CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Empty(h.Paths);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public async Task A_timeout_outside_the_range_is_refused_before_any_request(int seconds)
    {
        var h = Replies("{}");

        var r = await Make(h).GenerateAsync("m", "p", null, seconds, CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Empty(h.Paths);
    }

    [Fact]
    public async Task A_non_success_status_is_reported_with_the_code()
    {
        var h = new FakeHandler(_ => Task.FromResult(FakeHandler.Json("{\"error\":\"nope\"}", HttpStatusCode.InternalServerError)));

        var r = await Make(h).GenerateAsync("m", "p", null, 120, CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Equal(500, r.Status);
    }

    [Fact]
    public async Task A_200_without_the_response_field_is_an_error()
    {
        var r = await Make(Replies("{\"done\":true}")).GenerateAsync("m", "p", null, 120, CancellationToken.None);

        Assert.False(r.Ok);
    }

    [Fact]
    public async Task A_200_that_is_not_json_is_an_error()
    {
        var r = await Make(Replies("not json")).GenerateAsync("m", "p", null, 120, CancellationToken.None);

        Assert.False(r.Ok);
    }

    [Fact]
    public async Task A_warm_up_reply_is_retried_until_the_model_answers()
    {
        var calls = 0;
        var h = new FakeHandler(_ =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? FakeHandler.Json("{\"status\":\"warming_up\",\"retry_after\":0}")
                : FakeHandler.Json("{\"response\":\"ready\"}"));
        });

        var r = await Make(h).GenerateAsync("m", "p", null, 120, CancellationToken.None);

        Assert.True(r.Ok);
        Assert.Equal("ready", r.Text);
        Assert.Equal(2, h.Paths.Count);
    }

    [Fact]
    public async Task A_warm_up_that_never_ends_gives_up_after_ten_attempts()
    {
        var h = Replies("{\"status\":\"warming_up\",\"retry_after\":0}");

        var r = await Make(h).GenerateAsync("m", "p", null, 120, CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Equal(10, h.Paths.Count);
        Assert.Contains("warming_up", r.Error);
    }

    [Fact]
    public async Task A_reply_over_the_cap_is_cut_to_20000_characters_and_flagged()
    {
        var body = JsonSerializer.Serialize(new { response = new string('a', 20001) });

        var r = await Make(Replies(body)).GenerateAsync("m", "p", null, 120, CancellationToken.None);

        Assert.True(r.Ok);
        Assert.True(r.Capped);
        Assert.Equal(20000, r.Text!.Length);
    }

    [Fact]
    public async Task A_timeout_is_reported_as_timed_out()
    {
        var h = new FakeHandler(_ => throw new OperationCanceledException());

        var r = await Make(h).GenerateAsync("m", "p", null, 1, CancellationToken.None);

        Assert.False(r.Ok);
        Assert.True(r.TimedOut);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_reported_as_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var h = new FakeHandler(_ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Make(h).GenerateAsync("m", "p", null, 120, cts.Token));
    }
}
