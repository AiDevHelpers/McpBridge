using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class BrokerRuntimeTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sh-runtime-" + Guid.NewGuid());

    public BrokerRuntimeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    [Fact]
    public void A_fresh_heartbeat_is_alive()
    {
        var path = Path_("heartbeat.json");
        BrokerHeartbeat.Write(path, 4242, ProtectionMode.Tpm, Now);

        Assert.True(BrokerHeartbeat.IsAlive(path, Now.AddSeconds(2), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void An_old_heartbeat_is_not_alive()
    {
        var path = Path_("heartbeat.json");
        BrokerHeartbeat.Write(path, 4242, ProtectionMode.Tpm, Now);

        Assert.False(BrokerHeartbeat.IsAlive(path, Now.AddMinutes(1), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_missing_or_garbled_heartbeat_is_not_alive()
    {
        Assert.False(BrokerHeartbeat.IsAlive(Path_("none.json"), Now, TimeSpan.FromSeconds(5)));

        var garbled = Path_("garbled.json");
        File.WriteAllText(garbled, "{ nope");
        Assert.False(BrokerHeartbeat.IsAlive(garbled, Now, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_heartbeat_dated_in_the_future_is_not_alive()
    {
        var path = Path_("heartbeat.json");
        BrokerHeartbeat.Write(path, 4242, ProtectionMode.Tpm, Now.AddMinutes(10));

        Assert.False(BrokerHeartbeat.IsAlive(path, Now, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Heartbeat_ping_answers_from_the_heartbeat_file()
    {
        var path = Path_("heartbeat.json");
        BrokerHeartbeat.Write(path, 4242, ProtectionMode.Tpm, Now, @"C:\ProgramData\AiDevHelpers\script-helper\bin\scripthelper-broker.exe");

        Assert.Equal(BrokerState.Answering, new HeartbeatPing(path, () => Now.AddSeconds(1), _ => true).Check(TimeSpan.FromSeconds(3)));
        Assert.Equal(BrokerState.Silent, new HeartbeatPing(path, () => Now.AddMinutes(1), _ => true).Check(TimeSpan.FromSeconds(3)));
        Assert.Equal(BrokerState.Silent, new HeartbeatPing(Path_("absent.json"), () => Now, _ => true).Check(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void A_fresh_heartbeat_from_a_broker_outside_the_install_folder_is_untrusted()
    {
        var path = Path_("heartbeat.json");
        BrokerHeartbeat.Write(path, 4242, ProtectionMode.Tpm, Now, @"C:\Users\someone\Downloads\scripthelper-broker.exe");

        var state = new HeartbeatPing(path, () => Now, exe => exe.StartsWith(@"C:\ProgramData", StringComparison.OrdinalIgnoreCase)).Check(TimeSpan.FromSeconds(3));

        Assert.Equal(BrokerState.UntrustedInstall, state);
    }

    [Fact]
    public void A_heartbeat_held_open_by_another_process_is_skipped_not_fatal()
    {
        var path = Path_("heartbeat.json");
        BrokerHeartbeat.Write(path, 4242, ProtectionMode.Tpm, Now);

        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var written = BrokerHeartbeat.Write(path, 4242, ProtectionMode.Tpm, Now.AddSeconds(1));

        Assert.False(written);
    }

    [Fact]
    public void Reading_the_heartbeat_does_not_block_the_next_write()
    {
        var path = Path_("heartbeat.json");
        BrokerHeartbeat.Write(path, 4242, ProtectionMode.Tpm, Now);
        Assert.NotNull(BrokerHeartbeat.TryRead(path));

        Assert.True(BrokerHeartbeat.Write(path, 4242, ProtectionMode.Tpm, Now.AddSeconds(1)));
    }

    [Fact]
    public void Only_one_broker_holds_the_lock_at_a_time()
    {
        var name = "ScriptHelper.Test." + Guid.NewGuid().ToString("N");
        using var first = BrokerLock.TryAcquire(name);
        Assert.NotNull(first);

        // A named mutex belongs to the thread that holds it, and that thread may take it again. Task.Run can run
        // the attempt inline on this thread, which made the test flaky, so the second attempt gets its own thread.
        BrokerLock? second = null;
        var attempt = new Thread(() => second = BrokerLock.TryAcquire(name));
        attempt.Start();
        attempt.Join();

        Assert.Null(second);
    }

    [Fact]
    public void The_lock_can_be_taken_again_after_release()
    {
        var name = "ScriptHelper.Test." + Guid.NewGuid().ToString("N");
        BrokerLock.TryAcquire(name)!.Dispose();

        // A mutex must be released by the thread that acquired it, so acquire on this thread.
        using var again = BrokerLock.TryAcquire(name);

        Assert.NotNull(again);
    }

    [Fact]
    public async Task Host_writes_a_heartbeat_and_stops_when_the_stop_file_appears()
    {
        var heartbeat = Path_("heartbeat.json");
        var stop = Path_("broker.stop");
        var loop = new BrokerLoop(new RequestQueue(Path_("queue"), Path_("results")), _ => throw new InvalidOperationException());
        File.WriteAllText(stop, "");
        var host = new BrokerHost(loop, heartbeat, stop, TimeSpan.FromMinutes(5), () => Now, _ => { }, 4242, ProtectionMode.Tpm);

        var reason = await host.RunAsync();

        Assert.Equal("stopped", reason);
        Assert.False(File.Exists(stop), "the stop file is consumed");
        Assert.True(BrokerHeartbeat.IsAlive(heartbeat, Now, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Host_stops_after_the_idle_timeout_when_nothing_arrives()
    {
        var loop = new BrokerLoop(new RequestQueue(Path_("queue"), Path_("results")), _ => throw new InvalidOperationException());
        // Each clock read advances six minutes, so a 10-minute idle limit is passed within a few passes.
        var tick = 0;
        var host = new BrokerHost(loop, Path_("heartbeat.json"), Path_("broker.stop"), TimeSpan.FromMinutes(10),
            () => Now.AddMinutes(6 * tick++), _ => { }, 4242, ProtectionMode.Tpm);

        var reason = await host.RunAsync();

        Assert.Equal("idle timeout", reason);
    }

    [Fact]
    public async Task Host_answers_requests_and_is_not_idle_while_they_arrive()
    {
        var queue = new RequestQueue(Path_("queue"), Path_("results"));
        var sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
        var id = queue.Enqueue(sid, new BrokerRequestFile(@"C:\repo", "scripts/a.ps1", Array.Empty<string>()));
        var stop = Path_("broker.stop");
        var loop = new BrokerLoop(queue, _ => new BrokerResponse(true, "ran", 0, "out", ""));
        // The clock advances on every read, so the host goes idle and returns even if nothing is answered;
        // the test then fails on the assertion instead of hanging.
        var tick = 0;
        var host = new BrokerHost(loop, Path_("heartbeat.json"), stop, TimeSpan.FromMinutes(10),
            () => Now.AddMinutes(tick++), _ => { }, 4242, ProtectionMode.Tpm);
        // Ask the host to stop once the request has been answered, giving up after a deadline.
        var stopper = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (queue.TryReadResult(sid, id) is null && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            File.WriteAllText(stop, "");
        });

        var reason = await host.RunAsync();
        await Task.WhenAny(stopper, Task.Delay(TimeSpan.FromSeconds(15)));

        Assert.True(queue.TryReadResult(sid, id) is not null, "the request was never answered");
        Assert.True(queue.TryReadResult(sid, id)!.Ok);
        Assert.Contains(reason, new[] { "stopped", "idle timeout" });
    }

    [Fact]
    public void Audit_lines_are_appended_with_a_timestamp_and_never_overwrite()
    {
        var path = Path_("logs\\audit.log");

        AuditLog.Append(path, Now, "allowed one");
        AuditLog.Append(path, Now.AddSeconds(1), "denied two");

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("2026-11-21T12:00:00", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("allowed one", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("denied two", lines[1], StringComparison.Ordinal);
    }
}
