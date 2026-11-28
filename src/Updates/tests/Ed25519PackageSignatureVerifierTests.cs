using AiDevHelpers.Updates;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace AiDevHelpers.Updates.Tests;

public sealed class Ed25519PackageSignatureVerifierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "updsig-" + Guid.NewGuid().ToString("N"));
    private const string ManifestJson = "{\"Version\":\"2.0.0\",\"Files\":[]}";

    public Ed25519PackageSignatureVerifierTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static (Ed25519PrivateKeyParameters Priv, byte[] Pub) NewKey()
    {
        var priv = new Ed25519PrivateKeyParameters(new Org.BouncyCastle.Security.SecureRandom());
        return (priv, priv.GeneratePublicKey().GetEncoded());
    }

    private static byte[] Sign(Ed25519PrivateKeyParameters key, string text)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, key);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return signer.GenerateSignature();
    }

    private string WriteManifest(string json)
    {
        File.WriteAllText(Path.Combine(_dir, UpdateManifest.FileName), json);
        return _dir;
    }

    private static UpdateManifest Manifest() => UpdateManifest.TryParse(ManifestJson)!;

    [Fact]
    public async Task AcceptsSignatureFromKey()
    {
        var (priv, pub) = NewKey();
        string dir = WriteManifest(ManifestJson);
        File.WriteAllBytes(Path.Combine(dir, UpdateManifest.FileName + ".sig"), Sign(priv, ManifestJson));

        bool ok = await new Ed25519PackageSignatureVerifier(pub).VerifyAsync(dir, Manifest(), CancellationToken.None);

        Assert.True(ok);
    }

    [Fact]
    public async Task RejectsSignatureFromOtherKey()
    {
        var (_, pub) = NewKey();
        var (other, _) = NewKey();
        string dir = WriteManifest(ManifestJson);
        File.WriteAllBytes(Path.Combine(dir, UpdateManifest.FileName + ".sig"), Sign(other, ManifestJson));

        bool ok = await new Ed25519PackageSignatureVerifier(pub).VerifyAsync(dir, Manifest(), CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task RejectsASignatureOverAChangedManifest()
    {
        var (priv, pub) = NewKey();
        string dir = WriteManifest(ManifestJson.Replace("2.0.0", "9.9.9"));
        File.WriteAllBytes(Path.Combine(dir, UpdateManifest.FileName + ".sig"), Sign(priv, ManifestJson));

        bool ok = await new Ed25519PackageSignatureVerifier(pub).VerifyAsync(dir, Manifest(), CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task RejectsShortSignature()
    {
        var (_, pub) = NewKey();
        string dir = WriteManifest(ManifestJson);
        File.WriteAllBytes(Path.Combine(dir, UpdateManifest.FileName + ".sig"), new byte[10]);

        bool ok = await new Ed25519PackageSignatureVerifier(pub).VerifyAsync(dir, Manifest(), CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task ActiveKeyFromListAccepted()
    {
        var (_, retiredPub) = NewKey();
        var (active, activePub) = NewKey();
        string dir = WriteManifest(ManifestJson);
        File.WriteAllBytes(Path.Combine(dir, UpdateManifest.FileName + ".sig"), Sign(active, ManifestJson));
        var verifier = new Ed25519PackageSignatureVerifier(
        [
            new PackageSigningKey("old", retiredPub, Active: false),
            new PackageSigningKey("new", activePub, Active: true),
        ]);

        bool ok = await verifier.VerifyAsync(dir, Manifest(), CancellationToken.None);

        Assert.True(ok);
    }

    [Fact]
    public async Task RetiredKeyRejected()
    {
        var (retired, retiredPub) = NewKey();
        var (active, activePub) = NewKey();
        string dir = WriteManifest(ManifestJson);
        File.WriteAllBytes(Path.Combine(dir, UpdateManifest.FileName + ".sig"), Sign(retired, ManifestJson));
        var verifier = new Ed25519PackageSignatureVerifier(
        [
            new PackageSigningKey("old", retiredPub, Active: false),
            new PackageSigningKey("new", activePub, Active: true),
        ]);

        bool ok = await verifier.VerifyAsync(dir, Manifest(), CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public void KeyListNeedsAnActiveKey()
    {
        var (_, pub) = NewKey();

        Assert.Throws<ArgumentException>(() => new Ed25519PackageSignatureVerifier([new PackageSigningKey("old", pub, Active: false)]));
    }
}

