// The Host service (LocalSystem). It starts AiDevHelpers.MCP.Http in the user's session and restarts it with a bounded backoff
// (unit 2 of docs/plans/2026-11-26-tray-service.md). It never reads a manifest or any file the user can write.
using System.Diagnostics;
using AiDevHelpers.Helpers.WindowsSession;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "AiDevHelpers");
builder.Services.AddSingleton<ISessionLauncher, WindowsSessionLauncher>();
builder.Services.AddHostedService<McpSupervisor>();
await builder.Build().RunAsync();

/// <summary>Starts a process in a logged-on user's session. A test replaces it with a fake.</summary>
public interface ISessionLauncher
{
    Process? Start(uint session, string exe, string workingDirectory);
}

/// <summary>The real launcher: the user's own token, so the process runs in that session and is not elevated.</summary>
public sealed class WindowsSessionLauncher : ISessionLauncher
{
    public Process? Start(uint session, string exe, string workingDirectory) =>
        UserProcess.Start(new UserProcessStartInfo(exe, string.Empty, session, workingDirectory));
}

/// <summary>The restart policy: a bounded backoff, and a give-up after too many consecutive failures.</summary>
public static class RestartBackoff
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(32), TimeSpan.FromSeconds(60),
    ];

    /// <summary>The wait before the next start, or null to stop trying (a crash loop).</summary>
    public static TimeSpan? Next(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return TimeSpan.Zero;
        }

        if (consecutiveFailures > MaxFailures)
        {
            return null;
        }

        return Delays[Math.Min(consecutiveFailures - 1, Delays.Length - 1)];
    }

    /// <summary>Consecutive failures before the supervisor gives up.</summary>
    public const int MaxFailures = 8;
}

/// <summary>
/// What the supervisor does when there is no user session to start in (at boot, before anyone has logged on). That is not a failure of
/// MCP.Http, so it never counts toward the give-up limit: the supervisor checks again after a short wait for as long as the service runs.
/// </summary>
public static class SupervisorPolicy
{
    private static readonly TimeSpan NoSessionWait = TimeSpan.FromSeconds(5);

    public static (int Failures, TimeSpan Delay) AfterNoSession(int failures) => (failures, NoSessionWait);
}

/// <summary>Picks the session to start in: the console session when a user is logged on there, otherwise the first logged-on session.</summary>
public static class SessionChoice
{
    public static uint Choose(uint consoleSession, Func<uint, bool> hasUser, Func<uint> firstLoggedOn)
    {
        var console = UserSession.ChooseConsoleUserSession(consoleSession, hasUser);
        return console != UserSession.NoSession ? console : firstLoggedOn();
    }
}

/// <summary>Keeps one MCP.Http running in the user's session. A process that exits is started again after a backoff; a run that lasted
/// a minute resets the failure count. Stopping the service stops the process.</summary>
public sealed class McpSupervisor(ISessionLauncher launcher, IConfiguration configuration, ILogger<McpSupervisor> log) : BackgroundService
{
    private static readonly TimeSpan HealthyRun = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var exe = configuration["Mcp:Exe"] ?? Path.Combine(AppContext.BaseDirectory, "AiDevHelpers.MCP.Http.exe");
        var folder = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
        var failures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var session = SessionChoice.Choose(UserSession.ActiveConsoleSession(), s => UserSession.GetUserSid(s) is not null, UserSession.FindFirstLoggedOnSession);
            var started = DateTime.UtcNow;
            if (session == UserSession.NoSession)
            {
                // Nobody is logged on yet: wait and look again. This is not a failure and never ends the supervisor.
                log.LogInformation("No user session to start {Exe} in yet; checking again.", exe);
                var wait = SupervisorPolicy.AfterNoSession(failures);
                failures = wait.Failures;
                await Task.Delay(wait.Delay, stoppingToken);
                continue;
            }

            var process = launcher.Start(session, exe, folder);
            if (process is null)
            {
                // A session exists but the start failed: that is a failure and counts toward the give-up limit.
                log.LogWarning("Could not start {Exe} in session {Session}; trying again.", exe, session);
                failures++;
            }
            else
            {
                log.LogInformation("Started {Exe} (pid {Pid}) in session {Session}.", exe, process.Id, session);
                try
                {
                    await process.WaitForExitAsync(stoppingToken);
                    log.LogWarning("{Exe} exited with code {Code}.", exe, process.ExitCode);
                }
                catch (OperationCanceledException)
                {
                    StopProcess(process);
                    return;
                }

                failures = DateTime.UtcNow - started >= HealthyRun ? 0 : failures + 1;
            }

            var delay = RestartBackoff.Next(failures);
            if (delay is null)
            {
                log.LogError("{Exe} failed {Failures} times in a row; the supervisor has stopped restarting it.", exe, failures);
                return;
            }

            await Task.Delay(delay.Value, stoppingToken);
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }
}
