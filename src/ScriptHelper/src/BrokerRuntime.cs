using System.Text.Json;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// Heartbeat file the broker rewrites while it runs. The client treats a recent heartbeat as the
/// ping. The file sits in the admin-only data root, so a requester cannot fake it.
/// </summary>
public static class BrokerHeartbeat
{
    /// <summary>What the broker recorded about itself: its pid, its binary path and when it wrote this.</summary>
    public sealed record Snapshot(int Pid, string Exe, DateTime Utc);

    private sealed record Doc(int Pid, string Mode, DateTime Utc, string Exe);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// Writes the heartbeat. Returns false, without throwing, when another process has the file open
    /// and the replace is refused; the next beat tries again.
    /// </summary>
    public static bool Write(string path, int pid, ProtectionMode mode, DateTime utcNow, string exePath = "")
    {
        var doc = new Doc(pid, mode == ProtectionMode.Tpm ? "tpm" : "acl", utcNow.ToUniversalTime(), exePath);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(doc, Options));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Reads the heartbeat, or null when it is missing or unreadable.</summary>
    public static Snapshot? TryRead(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            // Share delete, so the broker can replace the file while a client is reading it.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var doc = JsonSerializer.Deserialize<Doc>(reader.ReadToEnd());
            return doc is null ? null : new Snapshot(doc.Pid, doc.Exe, doc.Utc);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static bool IsAlive(string path, DateTime utcNow, TimeSpan maxAge)
    {
        var snapshot = TryRead(path);
        if (snapshot is null)
            return false;

        var age = utcNow.ToUniversalTime() - snapshot.Utc;
        return age >= TimeSpan.Zero && age <= maxAge;
    }
}

/// <summary>
/// The client's ping: the broker is up when its heartbeat is fresh. A fresh heartbeat from a
/// binary that is not in the admin-only install folder is reported as untrusted, not answering.
/// </summary>
public sealed class HeartbeatPing : IBrokerPing
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);

    private readonly string _heartbeatPath;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<string, bool> _isInstalledSafely;

    public HeartbeatPing(string heartbeatPath, Func<DateTime> utcNow, Func<string, bool> isInstalledSafely)
    {
        _heartbeatPath = heartbeatPath;
        _utcNow = utcNow;
        _isInstalledSafely = isInstalledSafely;
    }

    public BrokerState Check(TimeSpan timeout)
    {
        var snapshot = BrokerHeartbeat.TryRead(_heartbeatPath);
        var now = _utcNow().ToUniversalTime();
        if (snapshot is null || now - snapshot.Utc < TimeSpan.Zero || now - snapshot.Utc > MaxAge)
            return BrokerState.Silent;

        return _isInstalledSafely(snapshot.Exe) ? BrokerState.Answering : BrokerState.UntrustedInstall;
    }
}

/// <summary>
/// A broker is trusted only when its binary sits directly in the admin-only install folder and
/// that folder, and the Script Helper data root above it, cannot be written by ordinary users.
/// </summary>
public static class BrokerInstall
{
    public static bool IsTrusted(string exePath, string installDirectory, string dataRoot)
    {
        if (string.IsNullOrEmpty(exePath))
            return false;

        var folder = Path.GetDirectoryName(Path.GetFullPath(exePath));
        if (folder is null || !string.Equals(folder, Path.GetFullPath(installDirectory), StringComparison.OrdinalIgnoreCase))
            return false;

        return File.Exists(exePath)
            && AdminOnlyDirectory.IsAdminOnly(installDirectory)
            && AdminOnlyDirectory.IsAdminOnly(dataRoot);
    }
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

    public async Task<string> RunAsync()
    {
        var lastActivity = _utcNow();
        while (true)
        {
            var now = _utcNow();
            BrokerHeartbeat.Write(_heartbeatPath, _pid, _mode, now, Environment.ProcessPath ?? string.Empty);

            if (File.Exists(_stopPath))
            {
                File.Delete(_stopPath);
                return Stopped("stopped");
            }

            if (_loop.RunOnce() > 0)
                lastActivity = now;

            if (now - lastActivity > _idleTimeout)
                return Stopped("idle timeout");

            await Task.Delay(Pause);
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
