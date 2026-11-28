using System.IO.Compression;
using System.Net;
using AiDevHelpers.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace AiDevHelpers.Updates.Tests;

public sealed class UpdateDownloaderTests : IDisposable
{
    private static readonly Uri GoodUrl = new("https://github.com/AiDevHelpers/McpBridge/releases/download/v2.0.0/AiDevHelpers-package-2.0.0.zip");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "upddl-" + Guid.NewGuid().ToString("N"));
    private readonly string _work = Path.Combine(Path.GetTempPath(), "upddlwork-" + Guid.NewGuid().ToString("N"));

    public UpdateDownloaderTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        foreach (string dir in new[] { _root, _work })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // Serves fixed bytes. finalUri simulates a redirect; sawRequest records whether any request was sent.
    private sealed class BytesHandler(byte[] body, Uri? finalUri = null) : HttpMessageHandler
    {
        public bool SawRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            SawRequest = true;
            var request2 = new HttpRequestMessage(HttpMethod.Get, finalUri ?? request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
                RequestMessage = request2,
            });
        }
    }

    // Builds a package for <paramref name="version"/>, signs its manifest with <paramref name="signer"/> (or the test key), and zips it.
    private (byte[] Zip, byte[] PublicKey) SignedZip(string version, Ed25519PrivateKeyParameters? signer = null)
    {
        var key = new Ed25519PrivateKeyParameters(new Org.BouncyCastle.Security.SecureRandom());
        string pkg = Path.Combine(_work, "pkg-" + version + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(pkg, "bin"));
        File.WriteAllText(Path.Combine(pkg, "bin", "app.exe"), "hello");
        UpdateManifestWriter.WriteManifest(pkg, version, notAfter: null);
        var sig = new Ed25519Signer();
        sig.Init(true, signer ?? key);
        byte[] manifest = File.ReadAllBytes(Path.Combine(pkg, UpdateManifest.FileName));
        sig.BlockUpdate(manifest, 0, manifest.Length);
        File.WriteAllBytes(Path.Combine(pkg, UpdateManifest.FileName + ".sig"), sig.GenerateSignature());

        string zip = Path.Combine(_work, Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(pkg, zip);
        return (File.ReadAllBytes(zip), key.GeneratePublicKey().GetEncoded());
    }

    private UpdateDownloader Downloader(HttpMessageHandler handler, byte[] publicKey, long maxBytes = UpdateDownloader.DefaultMaxBytes)
    {
        var verifier = new UpdatePackageVerifier(new Ed25519PackageSignatureVerifier(publicKey));
        var stager = new UpdateStager(_root, verifier, allowDevPackages: false, () => "1.0.0", NullLogger<UpdateStager>.Instance);
        return new UpdateDownloader(new HttpClient(handler), stager, _root, maxBytes);
    }

    private static GitHubReleaseAsset Asset(long size, Uri? url = null) =>
        new("AiDevHelpers-package-2.0.0.zip", size, url ?? GoodUrl);

    // Nothing may be staged for the version, and the download folder must be gone.
    private void AssertNothingStaged(string version)
    {
        Assert.False(Directory.Exists(Path.Combine(_root, version)));
        Assert.False(Directory.Exists(Path.Combine(_root, UpdateDownloader.DownloadFolderName)) &&
                     Directory.EnumerateFileSystemEntries(Path.Combine(_root, UpdateDownloader.DownloadFolderName)).Any());
    }

    [Fact]
    public async Task DownloadStagesVerifiedPackage()
    {
        var (zip, pub) = SignedZip("2.0.0");

        UpdateDownloadResult result = await Downloader(new BytesHandler(zip), pub)
            .DownloadAndStageAsync(Asset(zip.Length), "2.0.0", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(Directory.Exists(result.Stage!.StagedDir));
    }

    [Fact]
    public async Task SizeMismatchDeletesDownload()
    {
        var (zip, pub) = SignedZip("2.0.0");

        UpdateDownloadResult result = await Downloader(new BytesHandler(zip), pub)
            .DownloadAndStageAsync(Asset(zip.Length + 1), "2.0.0", CancellationToken.None);

        Assert.False(result.Success);
        AssertNothingStaged("2.0.0");
    }

    [Fact]
    public async Task OversizeDownloadAbortsAtCap()
    {
        var (zip, pub) = SignedZip("2.0.0");

        UpdateDownloadResult result = await Downloader(new BytesHandler(zip), pub, maxBytes: 10)
            .DownloadAndStageAsync(Asset(zip.Length), "2.0.0", CancellationToken.None);

        Assert.False(result.Success);
        AssertNothingStaged("2.0.0");
    }

    [Fact]
    public async Task NonHttpsUrlRejected()
    {
        var (zip, pub) = SignedZip("2.0.0");
        var handler = new BytesHandler(zip);

        UpdateDownloadResult result = await Downloader(handler, pub)
            .DownloadAndStageAsync(Asset(zip.Length, new Uri("http://github.com/o/r/x.zip")), "2.0.0", CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(handler.SawRequest);
    }

    [Fact]
    public async Task RedirectToOtherHostRejected()
    {
        var (zip, pub) = SignedZip("2.0.0");
        var handler = new BytesHandler(zip, new Uri("https://evil.example/package.zip"));

        UpdateDownloadResult result = await Downloader(handler, pub)
            .DownloadAndStageAsync(Asset(zip.Length), "2.0.0", CancellationToken.None);

        Assert.False(result.Success);
        AssertNothingStaged("2.0.0");
    }

    // The package is signed for 2.0.0 but the release tag says 2.1.0: nothing may be staged under either name.
    [Fact]
    public async Task VersionMismatchNotStaged()
    {
        var (zip, pub) = SignedZip("2.0.0");

        UpdateDownloadResult result = await Downloader(new BytesHandler(zip), pub)
            .DownloadAndStageAsync(Asset(zip.Length), "2.1.0", CancellationToken.None);

        Assert.False(result.Success);
        AssertNothingStaged("2.0.0");
        Assert.False(Directory.Exists(Path.Combine(_root, "2.1.0")));
    }

    [Fact]
    public async Task ZipWithBadSignatureNotStaged()
    {
        var other = new Ed25519PrivateKeyParameters(new Org.BouncyCastle.Security.SecureRandom());
        var (zip, pub) = SignedZip("2.0.0", signer: other);

        UpdateDownloadResult result = await Downloader(new BytesHandler(zip), pub)
            .DownloadAndStageAsync(Asset(zip.Length), "2.0.0", CancellationToken.None);

        Assert.False(result.Success);
        AssertNothingStaged("2.0.0");
    }
}
