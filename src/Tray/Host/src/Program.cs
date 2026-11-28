// The Host service (LocalSystem). It starts AiDevHelpers.MCP.Http in the user's session and restarts it with a bounded backoff
// (unit 2 of docs/plans/2026-11-26-tray-service.md). It installs signed updates (unit 10b) only from the admin-only updates folder,
// and only after the package's signature and hashes verify against the release key.
using System.Diagnostics;
using AiDevHelpers.Helpers.WindowsSession;
using AiDevHelpers.Host;
using AiDevHelpers.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "AiDevHelpers");
builder.Services.AddSingleton<ISessionLauncher, WindowsSessionLauncher>();
builder.Services.AddSingleton<McpHold>();
builder.Services.AddSingleton<IMcpProcessControl, McpProcessControl>();

// Updates (unit 10b). The install folder holds program files only; the updates folder is in ProgramData and admin-writable (UpdatesFolderAcl).
// Packages are accepted only with a valid signature from the release key (ReleaseSigningKeys).
string updatesRoot = Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "AiDevHelpers", "updates");
string installDir = AppContext.BaseDirectory;
var http = new HttpClient();
var verifier = new UpdatePackageVerifier(new Ed25519PackageSignatureVerifier([ReleaseSigningKeys.Current()]));
builder.Services.AddSingleton<IStagedUpdateInstaller>(sp => new StagedUpdateInstaller(
    verifier,
    new UpdateApplier(sp.GetRequiredService<ILogger<UpdateApplier>>()),
    new McpUpdateDrain(sp.GetRequiredService<IMcpProcessControl>(), TimeSpan.FromSeconds(30)),
    sp.GetRequiredService<IMcpProcessControl>(),
    new UpdateReplayRecord(updatesRoot),
    updatesRoot,
    installDir,
    sp.GetRequiredService<ILogger<StagedUpdateInstaller>>()));
builder.Services.AddSingleton<IGitHubReleaseSource>(new GitHubReleaseClient(http, "AiDevHelpers", "McpBridge"));
builder.Services.AddSingleton<IUpdateDownloader>(sp => new UpdateDownloader(
    http,
    new UpdateStager(updatesRoot, verifier, allowDevPackages: false, HostVersion.Current, sp.GetRequiredService<ILogger<UpdateStager>>()),
    updatesRoot));

// Hosted services start in this order, each start awaited: the startup install runs before the supervisor starts MCP.Http.
builder.Services.AddHostedService(sp => new StartupUpdateService(
    sp.GetRequiredService<IStagedUpdateInstaller>(),
    HostVersion.Current,
    updatesRoot,
    UpdatesFolderAcl.Apply,
    sp.GetRequiredService<ILogger<StartupUpdateService>>()));
builder.Services.AddHostedService<McpSupervisor>();
builder.Services.AddHostedService(sp => new StagedUpdateWatcher(
    sp.GetRequiredService<IGitHubReleaseSource>(),
    sp.GetRequiredService<IUpdateDownloader>(),
    sp.GetRequiredService<IStagedUpdateInstaller>(),
    HostVersion.Current,
    Environment.Exit,
    sp.GetRequiredService<ILogger<StagedUpdateWatcher>>()));
await builder.Build().RunAsync();

/// <summary>Starts a process in a logged-on user's session. A test replaces it with a fake.</summary>
public interface ISessionLauncher
{
    Process? Start(uint session, string exe, string arguments, string workingDirectory);
}

/// <summary>The real launcher: the user's own token, so the process runs in that session and is not elevated.</summary>
public sealed class WindowsSessionLauncher : ISessionLauncher
{
    public Process? Start(uint session, string exe, string arguments, string workingDirectory) =>
        UserProcess.Start(new UserProcessStartInfo(exe, arguments, session, workingDirectory));
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

    /// <summary>The argument that makes MCP.Http run as the HTTP server. The service passes it; a program launched any other way does not serve.</summary>
    public const string ServeArguments = "--serve";

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
public sealed class McpSupervisor(
    ISessionLauncher launcher,
    IConfiguration configuration,
    ILogger<McpSupervisor> log,
    McpHold hold,
    Func<uint>? chooseSession = null) : BackgroundService
{
    private static readonly TimeSpan HealthyRun = TimeSpan.FromMinutes(1);
    private readonly Func<uint> _chooseSession = chooseSession ?? (() => SessionChoice.Choose(UserSession.ActiveConsoleSession(), s => UserSession.GetUserSid(s) is not null, UserSession.FindFirstLoggedOnSession));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var exe = configuration["Mcp:Exe"] ?? Path.Combine(AppContext.BaseDirectory, "AiDevHelpers.MCP.Http.exe");
        var folder = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
        var failures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (hold.IsHeld)
            {
                // An update is applying: MCP.Http stays down until it is released.
                log.LogInformation("{Exe} is held for an update; not starting it.", exe);
                await hold.WhenChangedFrom(true).WaitAsync(stoppingToken);
                continue;
            }

            var session = _chooseSession();
            var started = DateTime.UtcNow;
            if (session == UserSession.NoSession)
            {
                // Nobody is logged on yet: wait and look again. This is not a failure and never ends the supervisor.
                log.LogInformation("No user session to start {Exe} in yet; checking again.", exe);
                var wait = SupervisorPolicy.AfterNoSession(failures);
                failures = wait.Failures;
                await PauseAsync(wait.Delay, stoppingToken);
                continue;
            }

            var process = launcher.Start(session, exe, SupervisorPolicy.ServeArguments, folder);
            if (process is null)
            {
                // A session exists but the start failed: that is a failure and counts toward the give-up limit.
                log.LogWarning("Could not start {Exe} in session {Session}; trying again.", exe, session);
                failures++;
            }
            else
            {
                log.LogInformation("Started {Exe} (pid {Pid}) in session {Session}.", exe, process.Id, session);
                hold.Attach(process);
                try
                {
                    if (await WaitForExitOrHoldAsync(process, stoppingToken))
                    {
                        // A hold stopped it for an update. That is not a failure, so the count is left alone.
                        StopProcess(process);
                        await process.WaitForExitAsync(stoppingToken);
                        log.LogInformation("{Exe} stopped for an update.", exe);
                        continue;
                    }

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

            await PauseAsync(delay.Value, stoppingToken);
        }
    }

    // Returns true when a hold asked for the process to stop; false when it exited on its own.
    private async Task<bool> WaitForExitOrHoldAsync(Process process, CancellationToken ct)
    {
        Task exit = process.WaitForExitAsync(ct);
        while (!exit.IsCompleted)
        {
            if (hold.IsHeld)
            {
                return true;
            }

            await Task.WhenAny(exit, hold.WhenChangedFrom(false));
        }

        return false;
    }

    // A backoff wait ends early when a hold starts, so the next pass waits on the hold instead of sleeping.
    private async Task PauseAsync(TimeSpan delay, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task pause = Task.Delay(delay, cts.Token);
        await Task.WhenAny(pause, hold.WhenChangedFrom(false));
        cts.Cancel();
        try
        {
            await pause;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Cut short by a hold, not by the service stopping.
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
