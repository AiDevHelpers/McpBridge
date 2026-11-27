using System.Net;
using Xunit;

namespace AiDevHelpers.Helpers.ModelServerHelper.Tests;

public sealed class ModelServerClientTests
{
    private const string Url = "http://localhost:11435";

    private static ModelServerClient Make(FakeHandler h, TimeProvider? clock = null) =>
        new(new HttpClient(h), new Uri(Url), clock ?? TimeProvider.System, TimeSpan.FromSeconds(5));

    private static Task<HttpResponseMessage> Route(HttpRequestMessage r) => r.RequestUri!.AbsolutePath switch
    {
        "/api/tags" => Task.FromResult(FakeHandler.Json("{\"models\":[{\"name\":\"HelperModel\"}]}")),
        "/api/ps" => Task.FromResult(FakeHandler.Json("{\"models\":[{\"name\":\"qwen\"}]}")),
        "/api/capacity" => Task.FromResult(FakeHandler.Json(
            "{\"cpuThreads\":8,\"gpuLayers\":20,\"gpu\":{\"detected\":true},\"maxConcurrentSummaryRequests\":3}")),
        _ => Task.FromResult(FakeHandler.Json("{}", HttpStatusCode.NotFound)),
    };

    [Fact]
    public async Task Status_healthy_and_loaded()
    {
        var s = await Make(new FakeHandler(Route)).GetStatusAsync();
        Assert.True(s.Running);
        Assert.True(s.Healthy);
        Assert.Equal(["HelperModel"], s.Models);
        Assert.Equal(["qwen"], s.Loaded);
        Assert.Equal("loaded", s.VramState);
    }

    [Fact]
    public async Task Status_idle_is_still_healthy()
    {
        var h = new FakeHandler(r => r.RequestUri!.AbsolutePath == "/api/ps"
            ? Task.FromResult(FakeHandler.Json("{\"models\":[]}")) : Route(r));
        var s = await Make(h).GetStatusAsync();
        Assert.True(s.Healthy);
        Assert.Equal("idle", s.VramState);
    }

    [Fact]
    public async Task Status_ps_failure_is_swallowed()
    {
        var h = new FakeHandler(r => r.RequestUri!.AbsolutePath == "/api/ps"
            ? throw new HttpRequestException("boom") : Route(r));
        var s = await Make(h).GetStatusAsync();
        Assert.True(s.Running);
        Assert.Empty(s.Loaded);
    }

    [Fact]
    public async Task Status_no_models_is_unhealthy()
    {
        var h = new FakeHandler(_ => Task.FromResult(FakeHandler.Json("{\"models\":[]}")));
        var s = await Make(h).GetStatusAsync();
        Assert.True(s.Running);
        Assert.False(s.Healthy);
    }

    [Fact]
    public async Task Status_unreachable_is_not_running()
    {
        var h = new FakeHandler(_ => throw new HttpRequestException("refused"));
        var s = await Make(h).GetStatusAsync();
        Assert.False(s.Running);
        Assert.False(s.Healthy);
    }

    [Fact]
    public async Task Status_tags_non_json_is_not_running()
    {
        var h = new FakeHandler(_ => Task.FromResult(FakeHandler.Json("<html>")));
        Assert.False((await Make(h).GetStatusAsync()).Running);
    }

    [Fact]
    public async Task Capacity_parses_fields()
    {
        var c = await Make(new FakeHandler(Route)).GetCapacityAsync();
        Assert.Equal(8, c.CpuThreads);
        Assert.Equal(20, c.GpuLayers);
        Assert.True(c.GpuDetected);
        Assert.Equal(3, c.MaxConcurrentSummaryRequests);
        Assert.False(c.FromFallback);
    }

    [Fact]
    public async Task Capacity_clamps_to_at_least_one()
    {
        var h = new FakeHandler(_ => Task.FromResult(FakeHandler.Json("{\"maxConcurrentSummaryRequests\":0}")));
        Assert.Equal(1, (await Make(h).GetCapacityAsync()).MaxConcurrentSummaryRequests);
    }

    [Theory]
    [InlineData(500, "{}")]
    [InlineData(200, "not json")]
    public async Task Capacity_non_2xx_or_bad_json_falls_back(int code, string body)
    {
        var h = new FakeHandler(_ => Task.FromResult(FakeHandler.Json(body, (HttpStatusCode)code)));
        var c = await Make(h).GetCapacityAsync();
        Assert.True(c.FromFallback);
        Assert.Equal(1, c.MaxConcurrentSummaryRequests);
        Assert.False(c.GpuDetected);
    }

    [Fact]
    public async Task Capacity_failure_is_not_cached()
    {
        var up = false;
        var h = new FakeHandler(r => up ? Route(r) : throw new HttpRequestException("down"));
        var client = Make(h);
        Assert.True((await client.GetCapacityAsync()).FromFallback);
        up = true;
        var second = await client.GetCapacityAsync();
        Assert.False(second.FromFallback);
        Assert.Equal(3, second.MaxConcurrentSummaryRequests);
    }

    [Fact]
    public async Task Capacity_success_is_cached_until_ttl_or_refresh()
    {
        var h = new FakeHandler(Route);
        var client = Make(h);
        await client.GetCapacityAsync();
        await client.GetCapacityAsync();
        Assert.Single(h.Paths, "/api/capacity");
        await client.GetCapacityAsync(refresh: true);
        Assert.Equal(2, h.Paths.Count(p => p == "/api/capacity"));
    }

    [Fact]
    public async Task Capacity_cache_expires_after_ttl()
    {
        var clock = new ManualClock();
        var h = new FakeHandler(Route);
        var client = Make(h, clock);
        await client.GetCapacityAsync();
        clock.Advance(TimeSpan.FromSeconds(61));
        await client.GetCapacityAsync();
        Assert.Equal(2, h.Paths.Count(p => p == "/api/capacity"));
    }

    [Fact]
    public async Task Cancellation_is_honored()
    {
        var h = new FakeHandler(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            return FakeHandler.Json("{}");
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Make(h).GetCapacityAsync(ct: cts.Token));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
