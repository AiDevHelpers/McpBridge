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
    private readonly Ed25519PublicKeyParameters[] _activeKeys;

    /// <summary>Creates the verifier for a single raw 32-byte Ed25519 public key.</summary>
    public Ed25519PackageSignatureVerifier(byte[] publicKey)
        : this([new PackageSigningKey("default", publicKey, Active: true)])
    {
    }

    /// <summary>
    /// Creates the verifier for a key list. A signature is accepted only when an active key verifies it, so a retired key
    /// (kept in the list for rotation history) cannot sign a package that installs.
    /// </summary>
    public Ed25519PackageSignatureVerifier(IEnumerable<PackageSigningKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var active = new List<Ed25519PublicKeyParameters>();
        foreach (PackageSigningKey key in keys)
        {
            ArgumentNullException.ThrowIfNull(key.PublicKey);
            if (key.PublicKey.Length != PublicKeyLength)
            {
                throw new ArgumentException($"Key '{key.KeyId}' is not a 32-byte Ed25519 public key.", nameof(keys));
            }

            if (key.Active)
            {
                active.Add(new Ed25519PublicKeyParameters(key.PublicKey, 0));
            }
        }

        if (active.Count == 0)
        {
            throw new ArgumentException("The key list has no active key.", nameof(keys));
        }

        _activeKeys = active.ToArray();
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

        foreach (Ed25519PublicKeyParameters key in _activeKeys)
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, key);
            verifier.BlockUpdate(manifestBytes, 0, manifestBytes.Length);
            if (verifier.VerifySignature(signature))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>One package signing key. A retired key stays listed for history but no longer verifies packages.</summary>
public sealed record PackageSigningKey(string KeyId, byte[] PublicKey, bool Active);

