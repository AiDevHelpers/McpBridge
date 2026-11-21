using System.Globalization;

namespace AiDevHelpers.ScriptHelper.Core;

public enum ProtectionMode
{
    Tpm,
    AclOnly,
}

public sealed record ModeDecision(bool Ok, ProtectionMode Mode, string Reason);

public static class ModeResolver
{
    /// <summary>
    /// Picks the protection mode at broker start. ACL-only requires an explicit opt-in, and a
    /// machine that once ran with a TPM never downgrades silently.
    /// </summary>
    public static ModeDecision Resolve(bool tpmPresent, ProtectionMode? lastRecorded, bool allowNoTpm)
    {
        if (tpmPresent)
            return new ModeDecision(true, ProtectionMode.Tpm, "TPM 2.0 present");

        if (lastRecorded == ProtectionMode.Tpm)
            return new ModeDecision(false, ProtectionMode.AclOnly, "TPM was previously seen; refusing ACL-only fallback");

        if (!allowNoTpm)
            return new ModeDecision(false, ProtectionMode.AclOnly, "no TPM 2.0; start with --allow-no-tpm to use ACL-only protection");

        return new ModeDecision(true, ProtectionMode.AclOnly, "ACL-only protection; no TPM 2.0");
    }
}

/// <summary>The recorded mode lives out of band in an admin-only file, not in the approvals file.</summary>
public static class ModeFile
{
    public static ProtectionMode? Read(string path)
    {
        if (!File.Exists(path))
            return null;

        return File.ReadAllText(path).Trim() switch
        {
            "tpm" => ProtectionMode.Tpm,
            "acl" => ProtectionMode.AclOnly,
            _ => null,
        };
    }

    public static void Write(string path, ProtectionMode mode)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, mode == ProtectionMode.Tpm ? "tpm" : "acl");
    }
}
