namespace AiDevHelpers.Updates;

/// <summary>Where the watcher learns the latest release. <see cref="GitHubReleaseClient"/> implements it.</summary>
public interface IGitHubReleaseSource
{
    Task<GitHubRelease?> GetLatestAsync(CancellationToken ct);
}

/// <summary>Downloads and stages a release's package. <see cref="UpdateDownloader"/> implements it.</summary>
public interface IUpdateDownloader
{
    Task<UpdateDownloadResult> DownloadAndStageAsync(GitHubReleaseAsset asset, string expectedVersion, CancellationToken ct);
}

/// <summary>Installs the newest staged package. <see cref="StagedUpdateInstaller"/> implements it.</summary>
public interface IStagedUpdateInstaller
{
    Task<StagedInstallResult> InstallNewestAsync(string currentVersion, bool allowDevPackages, CancellationToken ct);
}
