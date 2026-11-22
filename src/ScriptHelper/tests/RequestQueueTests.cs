using System.Text.Json;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class RequestQueueTests : IDisposable
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string OtherSid = "S-1-5-21-1111111111-2222222222-3333333333-1002";

    private readonly string _base = Path.Combine(Path.GetTempPath(), "sh-queue-" + Guid.NewGuid());
    private readonly string _queue;
    private readonly string _results;

    public RequestQueueTests()
    {
        _queue = Path.Combine(_base, "queue");
        _results = Path.Combine(_base, "results");
        Directory.CreateDirectory(_queue);
        Directory.CreateDirectory(_results);
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }

    private RequestQueue Queue() => new(_queue, _results);

    private static BrokerRequestFile Req(string script = "scripts/debug/reset.ps1") =>
        new(@"C:\repo", script, new[] { "-Port", "5000" });

    [Fact]
    public void Enqueue_writes_a_request_under_the_requesters_sid_folder()
    {
        var id = Queue().Enqueue(Sid, Req());

        var file = Path.Combine(_queue, Sid, id + ".request.json");
        Assert.True(File.Exists(file));
        var body = JsonSerializer.Deserialize<BrokerRequestFile>(File.ReadAllText(file))!;
        Assert.Equal(@"C:\repo", body.ProjectRoot);
        Assert.Equal("scripts/debug/reset.ps1", body.Script);
        Assert.Equal(new[] { "-Port", "5000" }, body.Args);
    }

    [Fact]
    public void Each_enqueue_gets_a_distinct_unguessable_id()
    {
        var q = Queue();

        var a = q.Enqueue(Sid, Req());
        var b = q.Enqueue(Sid, Req());

        Assert.NotEqual(a, b);
        Assert.True(Guid.TryParseExact(a, "N", out _), "id must be a GUID");
    }

    [Fact]
    public void Pending_lists_the_request_with_the_sid_of_the_folder_it_is_in()
    {
        var id = Queue().Enqueue(Sid, Req());

        var pending = Queue().Pending();

        var item = Assert.Single(pending);
        Assert.Equal(id, item.Id);
        Assert.Equal(Sid, item.RequesterSid);
        Assert.Equal("scripts/debug/reset.ps1", item.Request.Script);
    }

    [Fact]
    public void Pending_drops_a_request_once_its_result_is_written()
    {
        var q = Queue();
        var id = q.Enqueue(Sid, Req());

        q.WriteResult(Sid, id, new BrokerResponse(true, "ran", 0, "out", ""));

        Assert.Empty(q.Pending());
    }

    [Fact]
    public void A_result_is_readable_back_by_the_requester()
    {
        var q = Queue();
        var id = q.Enqueue(Sid, Req());

        q.WriteResult(Sid, id, new BrokerResponse(true, "ran", 7, "out", "err"));
        var r = q.TryReadResult(Sid, id);

        Assert.NotNull(r);
        Assert.Equal(7, r!.ExitCode);
        Assert.Equal("out", r.Stdout);
        Assert.Equal("err", r.Stderr);
    }

    [Fact]
    public void A_result_is_not_visible_to_another_sid()
    {
        var q = Queue();
        var id = q.Enqueue(Sid, Req());
        q.WriteResult(Sid, id, new BrokerResponse(true, "ran", 0, "out", ""));

        Assert.Null(q.TryReadResult(OtherSid, id));
    }

    [Fact]
    public void No_result_yet_reads_as_null()
    {
        var q = Queue();
        var id = q.Enqueue(Sid, Req());

        Assert.Null(q.TryReadResult(Sid, id));
    }

    [Fact]
    public void Folder_names_that_are_not_sids_are_ignored()
    {
        Directory.CreateDirectory(Path.Combine(_queue, "not-a-sid"));
        File.WriteAllText(Path.Combine(_queue, "not-a-sid", Guid.NewGuid().ToString("N") + ".request.json"),
            JsonSerializer.Serialize(Req()));

        Assert.Empty(Queue().Pending());
    }

    [Fact]
    public void A_malformed_request_is_skipped_without_stopping_the_others()
    {
        var q = Queue();
        Directory.CreateDirectory(Path.Combine(_queue, Sid));
        File.WriteAllText(Path.Combine(_queue, Sid, "bad.request.json"), "{ not json");
        var good = q.Enqueue(Sid, Req());

        var pending = q.Pending();

        var item = Assert.Single(pending);
        Assert.Equal(good, item.Id);
    }

    [Fact]
    public void A_malformed_request_is_answered_with_a_failure_so_the_requester_is_not_left_waiting()
    {
        var q = Queue();
        Directory.CreateDirectory(Path.Combine(_queue, Sid));
        File.WriteAllText(Path.Combine(_queue, Sid, "bad.request.json"), "{ not json");

        q.Pending();

        var r = q.TryReadResult(Sid, "bad");
        Assert.NotNull(r);
        Assert.False(r!.Ok);
        Assert.Contains("malformed", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailStale_answers_old_unanswered_requests_and_leaves_fresh_ones()
    {
        var q = Queue();
        var old = q.Enqueue(Sid, Req());
        var fresh = q.Enqueue(Sid, Req());
        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(Path.Combine(_queue, Sid, old + ".request.json"), now.AddHours(-2));
        File.SetLastWriteTimeUtc(Path.Combine(_queue, Sid, fresh + ".request.json"), now.AddSeconds(-5));

        var failed = q.FailStale(now, TimeSpan.FromMinutes(10));

        Assert.Equal(1, failed);
        var r = q.TryReadResult(Sid, old);
        Assert.NotNull(r);
        Assert.False(r!.Ok);
        Assert.Contains("stale", r.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(q.TryReadResult(Sid, fresh));
    }

    [Fact]
    public void FailStale_does_not_touch_requests_that_already_have_a_result()
    {
        var q = Queue();
        var id = q.Enqueue(Sid, Req());
        q.WriteResult(Sid, id, new BrokerResponse(true, "ran", 0, "out", ""));
        File.SetLastWriteTimeUtc(Path.Combine(_queue, Sid, id + ".request.json"), DateTime.UtcNow.AddHours(-2));

        Assert.Equal(0, q.FailStale(DateTime.UtcNow, TimeSpan.FromMinutes(10)));
        Assert.True(q.TryReadResult(Sid, id)!.Ok);
    }
}
