namespace AiDevHelpers.Updates;

/// <summary>
/// The release package signing key. The private half is the RELEASE_SIGNING_KEY secret of the protected GitHub "release" environment
/// (scripts/release-key-setup.ps1); only the public half is in the code. Release builds accept packages signed with this key only.
/// </summary>
public static class ReleaseSigningKeys
{
    /// <summary>Base64 of the raw 32-byte Ed25519 public key.</summary>
    public const string PublicKeyBase64 = "Z+ZbXOc2x8BkEZ+tOhoYoHv717Z4atBFpO4tz3FChGg=";

    /// <summary>The release key as a list entry, ready for <see cref="Ed25519PackageSignatureVerifier"/>.</summary>
    public static PackageSigningKey Current() =>
        new("release-2026-11", Convert.FromBase64String(PublicKeyBase64), Active: true);
}
