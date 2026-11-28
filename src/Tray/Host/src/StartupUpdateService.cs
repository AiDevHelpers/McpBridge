using AiDevHelpers.Updates;

namespace AiDevHelpers.Host;

/// <summary>
/// Runs once when the Host starts, before the supervisor (hosted services start in registration order and each start is awaited). It locks
/// the updates folder (admins and SYSTEM write, users read) and installs the newest staged package, so an update staged before a restart
/// is applied before MCP.Http starts. A failure is logged and the Host still starts.
/// </summary>
public sealed class StartupUpdateService(
    IStagedUpdateInstaller installer,
    Func<string> runningVersion,
    string updatesRoot,
    Action<string> lockFolder,
    ILogger<StartupUpdateService> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            lockFolder(updatesRoot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not lock the updates folder; updates are not installed this start.");
            return Task.CompletedTask;
        }

        try
        {
            StagedInstallResult result = installer.InstallNewestAsync(runningVersion(), allowDevPackages: false, cancellationToken).GetAwaiter().GetResult();
            log.LogInformation("Startup update check: {Outcome} {Message}", result.Outcome, result.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Startup update install failed; the Host starts without it.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
