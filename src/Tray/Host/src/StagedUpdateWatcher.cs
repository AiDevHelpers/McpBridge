using AiDevHelpers.Updates;

namespace AiDevHelpers.Host;

/// <summary>What one check did.</summary>
public enum StagedWatchOutcome
{
    /// <summary>No release newer than the running version.</summary>
    UpToDate,

    /// <summary>A newer release was downloaded and installed; the Host is exiting to restart on the new files.</summary>
    Installed,

    /// <summary>The check, download or install failed or was refused. The next check tries again.</summary>
    Failed,
}

/// <summary>
/// Checks GitHub for a newer release on a timer, downloads and installs it (unit 10b). After a successful install the Host exits with a
/// non-zero code, so the service recovery action restarts it on the new files; the running copy is never replaced while it runs.
/// Network errors are logged and retried at the next interval.
/// </summary>
public sealed class StagedUpdateWatcher(
    IGitHubReleaseSource source,
    IUpdateDownloader downloader,
    IStagedUpdateInstaller installer,
    Func<string> runningVersion,
    Action<int> exitHost,
    ILogger<StagedUpdateWatcher> log,
    TimeSpan? firstCheckDelay = null,
    TimeSpan? interval = null) : BackgroundService
{
    private static readonly TimeSpan DefaultFirstCheck = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);

    /// <summary>One check: find the release, download, install, and exit when the install applied.</summary>
    public async Task<StagedWatchOutcome> CheckOnceAsync(CancellationToken ct)
    {
        string running = runningVersion();

        GitHubRelease? release;
        try
        {
            release = await source.GetLatestAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning("Could not check for updates: {Error}", ex.Message);
            return StagedWatchOutcome.Failed;
        }

        if (release is null || !IsNewer(release.Version, running))
        {
            return StagedWatchOutcome.UpToDate;
        }

        UpdateDownloadResult download = await downloader.DownloadAndStageAsync(release.Package, release.Version, ct);
        if (!download.Success)
        {
            log.LogWarning("Update {Version} was not staged: {Message}", release.Version, download.Message);
            return StagedWatchOutcome.Failed;
        }

        StagedInstallResult install = await installer.InstallNewestAsync(running, allowDevPackages: false, ct);
        if (install.Outcome != StagedInstallOutcome.Applied)
        {
            log.LogWarning("Update {Version} was not installed: {Outcome} {Message}", release.Version, install.Outcome, install.Message);
            return StagedWatchOutcome.Failed;
        }

        log.LogInformation("Update {Version} installed; restarting the Host on the new files.", release.Version);
        exitHost(1);
        return StagedWatchOutcome.Installed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(firstCheckDelay ?? DefaultFirstCheck, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Update check failed; trying again at the next interval.");
            }

            await Task.Delay(interval ?? DefaultInterval, stoppingToken);
        }
    }

    // Compares numeric versions; a pre-release or build suffix is ignored, as the verifier does.
    private static bool IsNewer(string candidate, string running)
    {
        return Parse(candidate) is { } c && Parse(running) is { } r && c > r;

        static Version? Parse(string version)
        {
            string trimmed = version.TrimStart('v');
            int cut = trimmed.IndexOfAny(['-', '+']);
            return Version.TryParse(cut < 0 ? trimmed : trimmed[..cut], out Version? v) ? v : null;
        }
    }
}
