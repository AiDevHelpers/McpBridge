using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace AiDevHelpers.Updates;

/// <summary>
/// Verifies a detached Ed25519 signature over a package's manifest.json. The manifest lists the SHA-256 of every file,
/// so one signature covers the whole package. The signature is stored beside it as manifest.json.sig (64 raw bytes).
/// The public key comes from the caller (machine-only config for the dev key; the unit 10b key for release builds).
/// Why: .NET has no built-in Ed25519, so BouncyCastle does the check.
/// </summary>
public sealed class Ed25519PackageSignatureVerifier : IPackageSignatureVerifier
{
    private const string SignatureFileName = UpdateManifest.FileName + ".sig";
    private const int SignatureLength = 64;
    private const int PublicKeyLength = 32;
    private readonly byte[] _publicKey;

    /// <summary>Creates the verifier for a raw 32-byte Ed25519 public key.</summary>
    public Ed25519PackageSignatureVerifier(byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        if (publicKey.Length != PublicKeyLength)
        {
            throw new ArgumentException("An Ed25519 public key is 32 bytes.", nameof(publicKey));
        }

        _publicKey = publicKey;
    }

    /// <inheritdoc />
    public async Task<bool> VerifyAsync(string packageDir, UpdateManifest manifest, CancellationToken ct)
    {
        string manifestPath = Path.Combine(packageDir, UpdateManifest.FileName);
        string signaturePath = Path.Combine(packageDir, SignatureFileName);
        if (!File.Exists(manifestPath) || !File.Exists(signaturePath))
        {
            return false;
        }

        byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath, ct);
        byte[] signature = await File.ReadAllBytesAsync(signaturePath, ct);
        if (signature.Length != SignatureLength)
        {
            return false;
        }

        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(_publicKey, 0));
        verifier.BlockUpdate(manifestBytes, 0, manifestBytes.Length);
        return verifier.VerifySignature(signature);
    }
}

