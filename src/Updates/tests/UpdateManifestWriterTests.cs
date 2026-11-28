using AiDevHelpers.Updates;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace AiDevHelpers.Updates.Tests;

public sealed class UpdateManifestWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "updwriter-" + Guid.NewGuid().ToString("N"));

    public UpdateManifestWriterTests() => Directory.CreateDirectory(Path.Combine(_dir, "bin"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private void WriteFile(string relative, string content) =>
        File.WriteAllText(Path.Combine(_dir, relative.Replace('/', Path.DirectorySeparatorChar)), content);

    // The package is signed the way release-package.yml signs it, but with BouncyCastle so the test runs without openssl.
    [Fact]
    public async Task WriterOutputVerifiesAfterSigning()
    {
        WriteFile("bin/app.exe", "hello");
        UpdateManifestWriter.WriteManifest(_dir, "2.0.0", notAfter: null);
        var key = new Ed25519PrivateKeyParameters(new Org.BouncyCastle.Security.SecureRandom());
        var signer = new Ed25519Signer();
        signer.Init(true, key);
        byte[] manifest = File.ReadAllBytes(Path.Combine(_dir, UpdateManifest.FileName));
        signer.BlockUpdate(manifest, 0, manifest.Length);
        File.WriteAllBytes(Path.Combine(_dir, UpdateManifest.FileName + ".sig"), signer.GenerateSignature());
        byte[] pub = key.GeneratePublicKey().GetEncoded();

        var verifier = new UpdatePackageVerifier(new Ed25519PackageSignatureVerifier(pub));
        var result = await verifier.VerifyAsync(_dir, "1.0.0", allowDevPackages: false, CancellationToken.None);

        Assert.Equal(UpdatePackageStatus.Valid, result.Status);
    }

    [Fact]
    public void WriterSkipsManifestFiles()
    {
        WriteFile("bin/app.exe", "hello");
        WriteFile(UpdateManifest.FileName, "old");
        WriteFile(UpdateManifest.FileName + ".sig", "old");

        UpdateManifest manifest = UpdateManifestWriter.Build(_dir, "2.0.0", notAfter: null);

        Assert.Single(manifest.Files);
        Assert.Equal("bin/app.exe", manifest.Files[0].Path);
    }

    [Fact]
    public void WriterSetsNotAfter()
    {
        WriteFile("bin/app.exe", "hello");
        var notAfter = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

        UpdateManifest manifest = UpdateManifestWriter.Build(_dir, "2.0.0", notAfter);

        Assert.Equal(notAfter, manifest.NotAfter);
    }
}
