using System.Security.Cryptography;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>Signs approvals with a TPM-backed (or stand-in) ECDSA P-256 key. Signatures are hex, IEEE P1363.</summary>
public sealed class TpmApprovalSigner : IApprovalSigner
{
    private const DSASignatureFormat Format = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

    private readonly ECDsa _key;

    public TpmApprovalSigner(ECDsa key) => _key = key;

    public string Sign(byte[] payload) =>
        Convert.ToHexString(_key.SignData(payload, HashAlgorithmName.SHA256, Format));

    public bool Verify(byte[] payload, string signature)
    {
        if (string.IsNullOrEmpty(signature))
            return false;

        byte[] sig;
        try
        {
            sig = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        return _key.VerifyData(payload, sig, HashAlgorithmName.SHA256, Format);
    }
}

/// <summary>
/// ACL-only fallback for machines without a TPM. Approvals carry no signature; integrity rests on
/// the ProgramData ACLs. Only an empty signature verifies, so a stored signature is never trusted here.
/// </summary>
public sealed class AclOnlyApprovalSigner : IApprovalSigner
{
    public string Sign(byte[] payload) => string.Empty;

    public bool Verify(byte[] payload, string signature) =>
        string.Equals(signature, string.Empty, StringComparison.Ordinal);
}
