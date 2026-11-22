using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class ScriptClientTests : IDisposable
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(400);

    private readonly string _base = Path.Combine(Path.GetTempPath(), "sh-client-" + Guid.NewGuid());
    private readonly string _queueRoot;
    private readonly string _resultsRoot;

    public ScriptClientTests()
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

    private sealed class FakePing : IBrokerPing
    {
        public BrokerState State { get; set; } = BrokerState.Answering;
        public int Calls { get; private set; }

        public BrokerState Check(TimeSpan timeout)
        {
            Calls++;
            return State;
        }
    }

    /// <summary>Plays the broker: answers the one pending request for the SID with the given response.</summary>
    private void BrokerAnswers(RequestQueue q, BrokerResponse response)
    {
        var pending = Assert.Single(q.Pending());
        q.WriteResult(pending.RequesterSid, pending.Id, response);
    }

    [Fact]
    public void A_silent_broker_is_reported_before_anything_is_queued()
    {
        var q = Queue();
        var ping = new FakePing { State = BrokerState.Silent };

        var r = new ScriptClient(q, ping, Sid).Run(@"C:\repo", "scripts/a.ps1", Array.Empty<string>(), Short);

        Assert.False(r.Ok);
        Assert.Contains("not responding", r.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(q.Pending());
    }

    [Fact]
    public void A_broker_outside_the_admin_only_install_folder_is_refused_and_nothing_is_queued()
    {
        var q = Queue();
        var ping = new FakePing { State = BrokerState.UntrustedInstall };

        var r = new ScriptClient(q, ping, Sid).Run(@"C:\repo", "scripts/a.ps1", Array.Empty<string>(), Short);

        Assert.False(r.Ok);
        Assert.Contains("admin-only", r.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(q.Pending());
    }

    [Fact]
    public async Task The_request_is_queued_under_the_requesters_sid_and_the_answer_comes_back()
    {
        var q = Queue();
        var ping = new FakePing();
        var client = new ScriptClient(q, ping, Sid);

        // The broker answers from another thread while the client waits.
        var broker = Task.Run(async () =>
        {
            while (q.Pending().Count == 0) await Task.Delay(20);
            BrokerAnswers(q, new BrokerResponse(true, "ran", 0, "hello", ""));
        });

        var r = client.Run(@"C:\repo", "scripts/a.ps1", new[] { "-Port", "5000" }, TimeSpan.FromSeconds(20));
        await broker;

        Assert.True(r.Ok, r.Reason);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("hello", r.Stdout);
        Assert.Equal(1, ping.Calls);
    }

    [Fact]
    public async Task A_broker_exit_code_passes_through_to_the_caller()
    {
        var q = Queue();
        var client = new ScriptClient(q, new FakePing(), Sid);
        var broker = Task.Run(async () =>
        {
            while (q.Pending().Count == 0) await Task.Delay(20);
            BrokerAnswers(q, new BrokerResponse(true, "ran", 7, "", ""));
        });

        var r = client.Run(@"C:\repo", "scripts/a.ps1", Array.Empty<string>(), TimeSpan.FromSeconds(20));
        await broker;

        Assert.Equal(7, r.ExitCode);
    }

    [Fact]
    public async Task A_denial_from_the_broker_is_returned_with_its_reason()
    {
        var q = Queue();
        var client = new ScriptClient(q, new FakePing(), Sid);
        var broker = Task.Run(async () =>
        {
            while (q.Pending().Count == 0) await Task.Delay(20);
            BrokerAnswers(q, new BrokerResponse(false, "script is not in the allowlist", null, "", ""));
        });

        var r = client.Run(@"C:\repo", "scripts/evil.ps1", Array.Empty<string>(), TimeSpan.FromSeconds(20));
        await broker;

        Assert.False(r.Ok);
        Assert.Contains("not in the allowlist", r.Reason, StringComparison.Ordinal);
        Assert.Null(r.ExitCode);
    }

    [Fact]
    public void No_answer_in_time_is_reported_as_a_timeout_and_does_not_hang()
    {
        var q = Queue();
        var client = new ScriptClient(q, new FakePing(), Sid);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var r = client.Run(@"C:\repo", "scripts/a.ps1", Array.Empty<string>(), Short);

        Assert.False(r.Ok);
        Assert.Contains("timed out", r.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    [Fact]
    public void Each_run_submits_its_own_request()
    {
        var q = Queue();
        var client = new ScriptClient(q, new FakePing(), Sid);

        client.Run(@"C:\repo", "scripts/a.ps1", Array.Empty<string>(), Short);
        client.Run(@"C:\repo", "scripts/a.ps1", Array.Empty<string>(), Short);

        Assert.Equal(2, Directory.GetFiles(Path.Combine(_queueRoot, Sid), "*.request.json").Length);
    }
}
