using System.Text.Json;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// Heartbeat file the broker rewrites while it runs. The client treats a recent heartbeat as the
/// ping. The file sits in the admin-only data root, so a requester cannot fake it.
/// </summary>
public static class BrokerHeartbeat
{
    private sealed record Doc(int Pid, string Mode, DateTime Utc);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Write(string path, int pid, ProtectionMode mode, DateTime utcNow)
    {
        var doc = new Doc(pid, mode == ProtectionMode.Tpm ? "tpm" : "acl", utcNow.ToUniversalTime());
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(doc, Options));
        File.Move(temp, path, overwrite: true);
    }

    public static bool IsAlive(string path, DateTime utcNow, TimeSpan maxAge)
    {
        if (!File.Exists(path))
            return false;

        Doc? doc;
        try
        {
            doc = JsonSerializer.Deserialize<Doc>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }

        if (doc is null)
            return false;

        var age = utcNow.ToUniversalTime() - doc.Utc;
        return age >= TimeSpan.Zero && age <= maxAge;
    }
}

/// <summary>The client's ping: the broker is up when its heartbeat is fresh.</summary>
public sealed class HeartbeatPing : IBrokerPing
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);

    private readonly string _heartbeatPath;
    private readonly Func<DateTime> _utcNow;

    public HeartbeatPing(string heartbeatPath, Func<DateTime> utcNow)
    {
        _heartbeatPath = heartbeatPath;
        _utcNow = utcNow;
    }

    public bool Ping(TimeSpan timeout) => BrokerHeartbeat.IsAlive(_heartbeatPath, _utcNow(), MaxAge);
}

/// <summary>Single-instance lock held for the broker's lifetime (a named mutex).</summary>
public sealed class BrokerLock : IDisposable
{
    private readonly Mutex _mutex;

    private BrokerLock(Mutex mutex) => _mutex = mutex;

    public static BrokerLock? TryAcquire(string name)
    {
        var mutex = new Mutex(false, name);
        try
        {
            if (mutex.WaitOne(0))
                return new BrokerLock(mutex);
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died without releasing; this broker now owns the lock.
            return new BrokerLock(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

/// <summary>
/// The broker's main loop: answer pending requests, refresh the heartbeat, and stop when asked
/// (a stop file) or after it has been idle too long. Every stop is audited with its reason.
/// </summary>
public sealed class BrokerHost
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(20);

    private readonly BrokerLoop _loop;
    private readonly string _heartbeatPath;
    private readonly string _stopPath;
    private readonly TimeSpan _idleTimeout;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string> _audit;
    private readonly int _pid;
    private readonly ProtectionMode _mode;

    public BrokerHost(
        BrokerLoop loop,
        string heartbeatPath,
        string stopPath,
        TimeSpan idleTimeout,
        Func<DateTime> utcNow,
        Action<string> audit,
        int pid,
        ProtectionMode mode)
    {
        _loop = loop;
        _heartbeatPath = heartbeatPath;
        _stopPath = stopPath;
        _idleTimeout = idleTimeout;
        _utcNow = utcNow;
        _audit = audit;
        _pid = pid;
        _mode = mode;
    }

    public string Run()
    {
        var lastActivity = _utcNow();
        while (true)
        {
            var now = _utcNow();
            BrokerHeartbeat.Write(_heartbeatPath, _pid, _mode, now);

            if (File.Exists(_stopPath))
            {
                File.Delete(_stopPath);
                return Stopped("stopped");
            }

            if (_loop.RunOnce() > 0)
                lastActivity = now;

            if (now - lastActivity > _idleTimeout)
                return Stopped("idle timeout");

            Thread.Sleep(Pause);
        }
    }

    private string Stopped(string reason)
    {
        _audit($"broker {reason} (pid {_pid})");
        return reason;
    }
}

/// <summary>Appends timestamped lines to the audit log, creating its folder if needed.</summary>
public static class AuditLog
{
    public static void Append(string path, DateTime utcNow, string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, $"{utcNow.ToUniversalTime():O} {line}\n");
    }
}
