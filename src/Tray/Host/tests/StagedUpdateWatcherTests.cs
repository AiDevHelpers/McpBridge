using AiDevHelpers.Host;
using AiDevHelpers.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiDevHelpers.Host.Tests;

public sealed class StagedUpdateWatcherTests
{
    private static readonly GitHubReleaseAsset Asset = new("AiDevHelpers-package-2.0.0.zip", 10, new Uri("https://github.com/o/r/x.zip"));

    private sealed class FakeSource(GitHubRelease? release, Exception? error = null) : IGitHubReleaseSource
    {
        public Task<GitHubRelease?> GetLatestAsync(CancellationToken ct) =>
            error is null ? Task.FromResult(release) : Task.FromException<GitHubRelease?>(error);
    }

    // Counts calls. A test that must not download or install leaves the Result at its default, and the count proves it was not called.
    private sealed class FakeDownloader(bool success = true) : IUpdateDownloader
    {
        public int Calls { get; private set; }

        public Task<UpdateDownloadResult> DownloadAndStageAsync(GitHubReleaseAsset asset, string expectedVersion, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new UpdateDownloadResult(success, success ? "staged" : "failed", null));
        }
    }

    private sealed class FakeInstaller(StagedInstallOutcome outcome) : IStagedUpdateInstaller
    {
        public int Calls { get; private set; }

        // Set when the installer has returned, so the exit check can see the new files are already in place.
        public bool FilesInPlace { get; private set; }

        public Task<StagedInstallResult> InstallNewestAsync(string currentVersion, bool allowDevPackages, CancellationToken ct)
        {
            Calls++;
            FilesInPlace = outcome == StagedInstallOutcome.Applied;
            return Task.FromResult(new StagedInstallResult(outcome, "done", "2.0.0"));
        }
    }

    private static StagedUpdateWatcher Watcher(IGitHubReleaseSource source, IUpdateDownloader downloader, IStagedUpdateInstaller installer, Action<int> exit, string running = "1.0.0") =>
        new(source, downloader, installer, () => running, exit, NullLogger<StagedUpdateWatcher>.Instance);

    private static GitHubRelease Release(string version) =>
        new("v" + version, version, Asset);

    [Fact]
    public async Task CheckOnceInstallsNewerRelease()
    {
        var downloader = new FakeDownloader();
        var installer = new FakeInstaller(StagedInstallOutcome.Applied);
        var exits = new List<int>();

        var outcome = await Watcher(new FakeSource(Release("2.0.0")), downloader, installer, exits.Add).CheckOnceAsync(CancellationToken.None);

        Assert.Equal(StagedWatchOutcome.Installed, outcome);
        Assert.Equal(1, downloader.Calls);
        Assert.Equal(1, installer.Calls);
    }

    // The Host must not keep running the old code. After a successful apply it exits non-zero, which the service recovery action restarts.
    // The new files must already be in place at that moment.
    [Fact]
    public async Task AppliedUpdateExitsNonZeroAfterFilesInPlace()
    {
        var installer = new FakeInstaller(StagedInstallOutcome.Applied);
        bool filesInPlaceAtExit = false;
        var codes = new List<int>();
        void Exit(int code)
        {
            codes.Add(code);
            filesInPlaceAtExit = installer.FilesInPlace;
        }

        await Watcher(new FakeSource(Release("2.0.0")), new FakeDownloader(), installer, Exit).CheckOnceAsync(CancellationToken.None);

        Assert.Equal([1], codes);
        Assert.True(filesInPlaceAtExit);
    }

    [Fact]
    public async Task CheckOnceIgnoresSameOrOlderVersion()
    {
        var downloader = new FakeDownloader();
        var installer = new FakeInstaller(StagedInstallOutcome.Applied);

        var sameOutcome = await Watcher(new FakeSource(Release("1.0.0")), downloader, installer, _ => { }).CheckOnceAsync(CancellationToken.None);
        var olderOutcome = await Watcher(new FakeSource(Release("0.9.0")), downloader, installer, _ => { }).CheckOnceAsync(CancellationToken.None);

        Assert.Equal(StagedWatchOutcome.UpToDate, sameOutcome);
        Assert.Equal(StagedWatchOutcome.UpToDate, olderOutcome);
        Assert.Equal(0, downloader.Calls);
        Assert.Equal(0, installer.Calls);
    }

    // A network error is logged and the next check tries again. It must not throw or restart the Host.
    [Fact]
    public async Task CheckOnceSurvivesNetworkError()
    {
        var codes = new List<int>();

        var outcome = await Watcher(new FakeSource(null, new HttpRequestException("offline")), new FakeDownloader(), new FakeInstaller(StagedInstallOutcome.Applied), codes.Add)
            .CheckOnceAsync(CancellationToken.None);

        Assert.Equal(StagedWatchOutcome.Failed, outcome);
        Assert.Empty(codes);
    }
}
