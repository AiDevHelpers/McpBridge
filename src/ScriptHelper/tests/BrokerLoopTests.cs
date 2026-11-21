using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class BrokerLoopTests : IDisposable
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private readonly string _base = Path.Combine(Path.GetTempPath(), "sh-loop-" + Guid.NewGuid());
    private readonly string _queueRoot;
    private readonly string _resultsRoot;
    private readonly List<BrokerRequest> _seen = new();
    private Func<BrokerRequest, BrokerResponse> _respond = _ => new BrokerResponse(true, "ran", 0, "out", "");

    public BrokerLoopTests()
    {
        _queueRoot = Path.Combine(_base, "queue");
        _resultsRoot = Path.Combine(_base, "results");
        Directory.CreateDirectory(_queueRoot);
        Directory.CreateDirectory(_resultsRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }

    private RequestQueue Queue() => new(_queueRoot, _resultsRoot);

    private BrokerResponse Decide(BrokerRequest request)
    {
        _seen.Add(request);
        return _respond(request);
    }

    private BrokerLoop Loop(RequestQueue q) => new(q, Decide);

    private static BrokerRequestFile Req(string script = "scripts/debug/reset.ps1") =>
        new(@"C:\repo", script, Array.Empty<string>());

    [Fact]
    public void RunOnce_answers_every_pending_request_with_the_decision()
    {
        var q = Queue();
        var a = q.Enqueue(Sid, Req("scripts/debug/a.ps1"));
        var b = q.Enqueue(Sid, Req("scripts/debug/b.ps1"));

        var answered = Loop(q).RunOnce();

        Assert.Equal(2, answered);
        Assert.Equal(0, q.TryReadResult(Sid, a)!.ExitCode);
        Assert.Equal(0, q.TryReadResult(Sid, b)!.ExitCode);
        Assert.Equal(2, _seen.Count);
    }

    [Fact]
    public void The_decision_receives_the_requester_sid_from_the_folder()
    {
        var q = Queue();
        q.Enqueue(Sid, Req());

        Loop(q).RunOnce();

        var request = Assert.Single(_seen);
        Assert.Equal(Sid, request.RequesterSid);
        Assert.Equal(@"C:\repo", request.ProjectRoot);
        Assert.Equal("scripts/debug/reset.ps1", request.Script);
    }

    [Fact]
    public void A_decision_that_throws_is_answered_as_a_failure_and_the_loop_keeps_going()
    {
        var q = Queue();
        var boom = q.Enqueue(Sid, Req("scripts/debug/boom.ps1"));
        var fine = q.Enqueue(Sid, Req("scripts/debug/fine.ps1"));
        _respond = r => r.Script.Contains("boom", StringComparison.Ordinal)
            ? throw new InvalidOperationException("kaboom")
            : new BrokerResponse(true, "ran", 0, "out", "");

        var answered = Loop(q).RunOnce();

        Assert.Equal(2, answered);
        var failed = q.TryReadResult(Sid, boom);
        Assert.NotNull(failed);
        Assert.False(failed!.Ok);
        Assert.Contains("internal error", failed.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(q.TryReadResult(Sid, fine)!.Ok);
    }

    [Fact]
    public void RunOnce_with_nothing_pending_answers_nothing()
    {
        Assert.Equal(0, Loop(Queue()).RunOnce());
        Assert.Empty(_seen);
    }

    [Fact]
    public void A_request_answered_in_one_pass_is_not_decided_again()
    {
        var q = Queue();
        q.Enqueue(Sid, Req());
        var loop = Loop(q);

        loop.RunOnce();
        loop.RunOnce();

        Assert.Single(_seen);
    }
}
