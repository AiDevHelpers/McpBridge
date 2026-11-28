using AiDevHelpers.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace AiDevHelpers.Updates.Tests;

public sealed class StagedUpdateInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "updinst-" + Guid.NewGuid().ToString("N"));
    private readonly string _updates;
    private readonly string _install;
    private readonly Ed25519PrivateKeyParameters _key = new(new Org.BouncyCastle.Security.SecureRandom());

    public StagedUpdateInstallerTests()
    {
        _updates = Path.Combine(_root, "updates");
        _install = Path.Combine(_root, "install");
        Directory.CreateDirectory(_updates);
        Directory.CreateDirectory(Path.Combine(_install, "bin"));
        File.WriteAllText(Path.Combine(_install, "bin", "app.exe"), "old");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // Records hold and release calls. The process stops (or does not) as the test says.
    private sealed class FakeControl(bool stops) : IMcpProcessControl
    {
        public int Holds { get; private set; }
        public int Releases { get; private set; }

        public void Hold() => Holds++;

        public void Release() => Releases++;

        public Task<bool> WaitUntilStoppedAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(stops);
    }

    private StagedUpdateInstaller Installer(bool stops = true)
    {
        var control = new FakeControl(stops);
        var verifier = new UpdatePackageVerifier(new Ed25519PackageSignatureVerifier(_key.GeneratePublicKey().GetEncoded()));
        return new StagedUpdateInstaller(
            verifier,
            new UpdateApplier(NullLogger<UpdateApplier>.Instance),
            new McpUpdateDrain(control, TimeSpan.FromSeconds(30)),
            control,
            new UpdateReplayRecord(_updates),
            _updates,
            _install,
            NullLogger<StagedUpdateInstaller>.Instance);
    }

    // Stages a signed package folder named for its version. Its file content names the version, so tests can see which one was installed.
    private string Stage(string version)
    {
        string dir = Path.Combine(_updates, version);
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        File.WriteAllText(Path.Combine(dir, "bin", "app.exe"), "version " + version);
        UpdateManifestWriter.WriteManifest(dir, version, notAfter: null);
        var signer = new Ed25519Signer();
        signer.Init(true, _key);
        byte[] manifest = File.ReadAllBytes(Path.Combine(dir, UpdateManifest.FileName));
        signer.BlockUpdate(manifest, 0, manifest.Length);
        File.WriteAllBytes(Path.Combine(dir, UpdateManifest.FileName + ".sig"), signer.GenerateSignature());
        return dir;
    }

    private string InstalledApp() => File.ReadAllText(Path.Combine(_install, "bin", "app.exe"));

    [Fact]
    public async Task InstallAppliesStagedPackage()
    {
        string staged = Stage("2.0.0");

        StagedInstallResult result = await Installer().InstallNewestAsync("1.0.0", allowDevPackages: false, CancellationToken.None);

        Assert.Equal(StagedInstallOutcome.Applied, result.Outcome);
        Assert.Equal("version 2.0.0", InstalledApp());
        Assert.False(Directory.Exists(staged));
    }

    // The package fails while being applied: the install folder must be unchanged, and the package set aside.
    [Fact]
    public async Task FailedApplyRollsBackAndRejects()
    {
        // The package wants bin/app.exe, but the install has a directory there, so the move fails and the applier rolls back.
        Directory.Delete(Path.Combine(_install, "bin"), recursive: true);
        Directory.CreateDirectory(Path.Combine(_install, "bin", "app.exe"));
        File.WriteAllText(Path.Combine(_install, "bin", "app.exe", "keep.txt"), "keep");
        string staged = Stage("2.0.0");

        StagedInstallResult result = await Installer().InstallNewestAsync("1.0.0", allowDevPackages: false, CancellationToken.None);

        Assert.Equal(StagedInstallOutcome.Failed, result.Outcome);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_install, "bin", "app.exe", "keep.txt")));
        Assert.False(Directory.Exists(staged));
        Assert.True(Directory.Exists(staged + StagedUpdateInstaller.RejectedSuffix));
    }

    [Fact]
    public async Task FailedApplyIsNotRetried()
    {
        Directory.Delete(Path.Combine(_install, "bin"), recursive: true);
        Directory.CreateDirectory(Path.Combine(_install, "bin", "app.exe"));
        Stage("2.0.0");
        var installer = Installer();
        await installer.InstallNewestAsync("1.0.0", allowDevPackages: false, CancellationToken.None);

        StagedInstallResult second = await installer.InstallNewestAsync("1.0.0", allowDevPackages: false, CancellationToken.None);

        Assert.Equal(StagedInstallOutcome.None, second.Outcome);
    }

    // The same package offered again (here at the same version, with dev packages allowed) must not install twice.
    [Fact]
    public async Task ReplayAtSameVersionNotInstalledTwice()
    {
        Stage("2.0.0");
        var installer = Installer();
        Assert.Equal(StagedInstallOutcome.Applied,
            (await installer.InstallNewestAsync("1.0.0", allowDevPackages: true, CancellationToken.None)).Outcome);
        File.WriteAllText(Path.Combine(_install, "bin", "app.exe"), "changed after install");
        Stage("2.0.0");

        StagedInstallResult second = await installer.InstallNewestAsync("2.0.0", allowDevPackages: true, CancellationToken.None);

        Assert.Equal(StagedInstallOutcome.Rejected, second.Outcome);
        Assert.Equal("changed after install", InstalledApp());
    }

    // A drain that times out must keep the package staged for a later try, and must not apply it.
    [Fact]
    public async Task DrainTimeoutKeepsPackageStaged()
    {
        string staged = Stage("2.0.0");

        StagedInstallResult result = await Installer(stops: false).InstallNewestAsync("1.0.0", allowDevPackages: false, CancellationToken.None);

        Assert.Equal(StagedInstallOutcome.DrainFailed, result.Outcome);
        Assert.True(Directory.Exists(staged));
        Assert.Equal("old", InstalledApp());
    }

    [Fact]
    public async Task StartupAppliesNewestStaged()
    {
        Stage("2.0.0");
        Stage("2.1.0");

        StagedInstallResult result = await Installer().InstallNewestAsync("1.0.0", allowDevPackages: false, CancellationToken.None);

        Assert.Equal(StagedInstallOutcome.Applied, result.Outcome);
        Assert.Equal("2.1.0", result.Version);
        Assert.Equal("version 2.1.0", InstalledApp());
    }

    // The newest staged package is damaged: it is set aside, and the next start installs the older valid one.
    [Fact]
    public async Task StartupSkipsInvalidStaged()
    {
        string broken = Stage("2.1.0");
        Stage("2.0.0");
        File.WriteAllText(Path.Combine(broken, "bin", "app.exe"), "tampered");
        var installer = Installer();

        StagedInstallResult first = await installer.InstallNewestAsync("1.0.0", allowDevPackages: false, CancellationToken.None);
        StagedInstallResult second = await installer.InstallNewestAsync("1.0.0", allowDevPackages: false, CancellationToken.None);

        Assert.Equal(StagedInstallOutcome.Rejected, first.Outcome);
        Assert.Equal(StagedInstallOutcome.Applied, second.Outcome);
        Assert.Equal("version 2.0.0", InstalledApp());
    }

    // Download and staging folders are not packages, even when they hold a valid manifest.
    [Fact]
    public async Task DownloadAndStagingFoldersIgnored()
    {
        string hidden = Path.Combine(_updates, UpdateDownloader.DownloadFolderName, "abc");
        Directory.CreateDirectory(hidden);
        UpdateManifestWriter.WriteManifest(hidden, "9.9.9", notAfter: null);

        StagedInstallResult result = await Installer().InstallNewestAsync("1.0.0", allowDevPackages: false, CancellationToken.None);

        Assert.Equal(StagedInstallOutcome.None, result.Outcome);
    }

    // Installing writes only inside the install folder and the updates folder: a file outside both is never touched.
    [Fact]
    public async Task InstallNeverWritesOutsideInstallDir()
    {
        string outside = Path.Combine(_root, "programdata-config.json");
        File.WriteAllText(outside, "{\"port\":5058}");
        Stage("2.0.0");

        await Installer().InstallNewestAsync("1.0.0", allowDevPackages: false, CancellationToken.None);

        Assert.Equal("{\"port\":5058}", File.ReadAllText(outside));
    }
}
