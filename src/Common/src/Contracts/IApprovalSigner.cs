namespace AiDevHelpers.Common.Contracts;

/// <summary>
///     Signs and verifies approval payloads for the Script Helper. A TPM-backed signer and an
///     ACL-only (unsigned) signer implement it.
/// </summary>
public interface IApprovalSigner
{
    string Sign(byte[] payload);

    bool Verify(byte[] payload, string signature);
}
