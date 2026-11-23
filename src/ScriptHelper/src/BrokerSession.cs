using System.ComponentModel;
using System.Diagnostics;

namespace AiDevHelpers.ScriptHelper.Core;

public enum StartOutcome
{
    Started,
    Declined,
}

/// <summary>Starts the broker elevated. The seam keeps <see cref="BrokerSession"/> testable without a UAC prompt.</summary>
public interface IBrokerProcessStarter
{
    StartOutcome StartElevated(string exePath);
}

/// <summary>Starts the broker hidden with the "runas" verb: one UAC prompt, and the broker then stays up for later requests.</summary>
public sealed class RunAsBrokerStarter : IBrokerProcessStarter
{
    private const int ErrorCancelled = 1223;

    public StartOutcome StartElevated(string exePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exePath)
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return StartOutcome.Started;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return StartOutcome.Declined;
        }
    }
}

public sealed record BrokerSessionResult(bool Ok, string Reason);

/// <summary>
/// Keeps one elevated broker running for the session. The first elevated request starts it with one UAC prompt; later requests
/// find it answering and start nothing. A declined prompt is not repeated until the cooldown has passed, and concurrent
/// requests share one start. A broker whose binary is not in the trusted install folder is refused, never started over.
/// </summary>
public sealed class BrokerSession
{
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IBrokerPing _ping;
    private readonly IBrokerProcessStarter _starter;
    private readonly string _brokerExe;
    private readonly TimeSpan _startTimeout;
    private readonly TimeSpan _declineCooldown;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime? _declinedAt;

    public BrokerSession(
        IBrokerPing ping,
        IBrokerProcessStarter starter,
        string brokerExe,
        TimeSpan startTimeout,
        TimeSpan declineCooldown,
        Func<DateTime> utcNow,
        Func<TimeSpan, Task> delay)
    {
        _ping = ping;
        _starter = starter;
        _brokerExe = brokerExe;
        _startTimeout = startTimeout;
        _declineCooldown = declineCooldown;
        _utcNow = utcNow;
        _delay = delay;
    }

    public async Task<BrokerSessionResult> EnsureRunningAsync(CancellationToken ct)
    {
        if (Check() is { } early)
        {
            return early;
        }

        await _gate.WaitAsync(ct);
        try
        {
            // Another caller may have started it while this one waited.
            if (Check() is { } now)
            {
                return now;
            }

            if (_declinedAt is { } declined && _utcNow() - declined < _declineCooldown)
            {
                return new BrokerSessionResult(false, "the elevation prompt was declined; try again after the cooldown");
            }

            if (_starter.StartElevated(_brokerExe) == StartOutcome.Declined)
            {
                _declinedAt = _utcNow();
                return new BrokerSessionResult(false, "the elevation prompt was declined");
            }

            _declinedAt = null;
            var waited = TimeSpan.Zero;
            while (waited < _startTimeout)
            {
                ct.ThrowIfCancellationRequested();
                await _delay(PollInterval);
                waited += PollInterval;
                if (Check() is { } up)
                {
                    return up;
                }
            }

            return new BrokerSessionResult(false, "the broker did not answer after it was started");
        }
        finally
        {
            _gate.Release();
        }
    }

    // Ok when the broker answers, a refusal when its install is untrusted, null while it is silent.
    private BrokerSessionResult? Check() => _ping.Check(PingTimeout) switch
    {
        BrokerState.Answering => new BrokerSessionResult(true, "answering"),
        BrokerState.UntrustedInstall => new BrokerSessionResult(false, "the broker is not installed in the trusted folder"),
        _ => null,
    };
}
