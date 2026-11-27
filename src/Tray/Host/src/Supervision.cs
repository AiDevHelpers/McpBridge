using System.Runtime.Versioning;
using AiDevHelpers.Helpers.WindowsSession;

namespace AiDevHelpers.Host;

/// <summary>Where the logged-on users are. The real one asks Windows; tests use a fake.</summary>
public interface ISessionSource
{
    /// <summary>The active console session, or <see cref="UserSession.NoSession"/>.</summary>
    uint ConsoleSession { get; }

    /// <summary>True when a user is logged on to the session.</summary>
    bool HasUser(uint session);

    /// <summary>The first active session with a logged-on user (RDP, for example), or <see cref="UserSession.NoSession"/>.</summary>
    uint FirstLoggedOnSession();
}

/// <summary>Chooses the session the HTTP MCP runs in.</summary>
[SupportedOSPlatform("windows")]
public static class SessionPicker
{
    /// <summary>The console session when it has a user, else the first active session with one, else <see cref="UserSession.NoSession"/>.</summary>
    public static uint Pick(ISessionSource sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var chosen = UserSession.ChooseConsoleUserSession(sessions.ConsoleSession, sessions.HasUser);
        return chosen != UserSession.NoSession ? chosen : sessions.FirstLoggedOnSession();
    }
}

/// <summary>A process started in a user's session.</summary>
public interface IChildProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void Kill();
}

/// <summary>Starts a process in a user's session.</summary>
public interface ISessionLauncher
{
    /// <summary>The started process, or null when Windows reports none (it already exited).</summary>
    IChildProcess? Start(string fileName, string arguments, uint sessionId, string workingDirectory);
}

/// <summary>Asks the running server whether it is well.</summary>
public interface IHealthProbe
{
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken);
}

/// <summary>Waits. Tests replace it so no test sleeps.</summary>
public interface IDelay
{
    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>The real delay.</summary>
public sealed class SystemDelay : IDelay
{
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) => Task.Delay(duration, cancellationToken);
}

/// <summary>How long to wait before restarting a server that keeps failing: 1 s, 1 s, 2 s, 4 s, doubling up to 1 minute.</summary>
public static class RestartBackoff
{
    private static readonly TimeSpan Maximum = TimeSpan.FromSeconds(60);

    public static TimeSpan For(int consecutiveFailures)
    {
        if (consecutiveFailures <= 1)
        {
            return TimeSpan.FromSeconds(1);
        }

        var seconds = Math.Pow(2, Math.Min(consecutiveFailures - 1, 10));
        return TimeSpan.FromSeconds(Math.Min(seconds, Maximum.TotalSeconds));
    }
}

/// <summary>What to start, and how patiently to watch it.</summary>
public sealed record SupervisorOptions(string FileName, string Arguments, string WorkingDirectory)
{
    /// <summary>How often the supervisor looks at the session and the health of the server.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Polls after a start before health is checked, so a server that is still starting is not restarted.</summary>
    public int HealthGracePolls { get; init; } = 3;

    /// <summary>Consecutive failed health checks that restart the server.</summary>
    public int UnhealthyLimit { get; init; } = 3;

    /// <summary>Polls a server must stay up for its failure count to reset.</summary>
    public int StablePolls { get; init; } = 12;

    /// <summary>The longest the supervisor holds back a start while the servers are closing for an update.</summary>
    public TimeSpan MaxHold { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>What the supervisor is doing.</summary>
public enum SupervisorState
{
    Stopped,
    NoUserSession,
    Held,
    Starting,
    Running,
    Backoff,
}

/// <summary>
/// Keeps one HTTP MCP running in the logged-on user's session. It starts the server when a user is there, moves it
/// when the user's session changes, restarts it after a crash with a growing delay, restarts it when its health
/// checks fail, and holds back while the servers are closing for an update (up to a limit).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class McpSupervisor(
    SupervisorOptions options,
    ISessionSource sessions,
    ISessionLauncher launcher,
    IHealthProbe health,
    Func<bool> isClosing,
    IDelay delay)
{
    private volatile SupervisorState _state = SupervisorState.Stopped;

    public SupervisorState State => _state;

    /// <summary>Runs until cancelled. The server is stopped on the way out.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        var held = TimeSpan.Zero;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var session = SessionPicker.Pick(sessions);
                if (session == UserSession.NoSession)
                {
                    _state = SupervisorState.NoUserSession;
                    await delay.DelayAsync(options.PollInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (isClosing() && held < options.MaxHold)
                {
                    _state = SupervisorState.Held;
                    held += options.PollInterval;
                    await delay.DelayAsync(options.PollInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                held = TimeSpan.Zero;
                _state = SupervisorState.Starting;
                var polls = await RunServerAsync(session, cancellationToken).ConfigureAwait(false);
                failures = polls >= options.StablePolls ? 1 : failures + 1;

                _state = SupervisorState.Backoff;
                await delay.DelayAsync(RestartBackoff.For(failures), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _state = SupervisorState.Stopped;
        }
    }

    /// <summary>Starts the server and watches it until it exits, moves or fails. Returns how many polls it stayed up.</summary>
    private async Task<int> RunServerAsync(uint session, CancellationToken cancellationToken)
    {
        IChildProcess? child;
        try
        {
            child = launcher.Start(options.FileName, options.Arguments, session, options.WorkingDirectory);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return 0;
        }

        if (child is null)
        {
            return 0;
        }

        using (child)
        {
            _state = SupervisorState.Running;
            var polls = 0;
            var unhealthy = 0;
            try
            {
                while (!child.HasExited)
                {
                    var exited = child.WaitForExitAsync(cancellationToken);
                    var tick = delay.DelayAsync(options.PollInterval, cancellationToken);
                    if (await Task.WhenAny(exited, tick).ConfigureAwait(false) == exited)
                    {
                        break;
                    }

                    polls++;
                    if (SessionPicker.Pick(sessions) != session)
                    {
                        break;
                    }

                    if (polls > options.HealthGracePolls)
                    {
                        unhealthy = await health.IsHealthyAsync(cancellationToken).ConfigureAwait(false) ? 0 : unhealthy + 1;
                        if (unhealthy >= options.UnhealthyLimit)
                        {
                            break;
                        }
                    }
                }
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill();
                }
            }

            return polls;
        }
    }
}
