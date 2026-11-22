using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>Reports whether a TPM 2.0 is present, using the TPM Base Services (tbs.dll) device info call.</summary>
[SupportedOSPlatform("windows")]
public static class TbsTpmDetector
{
    private const uint TpmVersion20 = 2;

    public static bool Tpm20Present()
    {
        var info = new TpmDeviceInfo { StructVersion = 1 };
        var rc = Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TpmDeviceInfo>(), ref info);
        return rc == 0 && info.TpmVersion == TpmVersion20;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TpmDeviceInfo
    {
        public uint StructVersion;
        public uint TpmVersion;
        public uint TpmInterfaceType;
        public uint TpmImpRevision;
    }

    [DllImport("tbs.dll")]
    private static extern uint Tbsi_GetDeviceInfo(uint size, ref TpmDeviceInfo info);
}

/// <summary>
/// Non-exportable ECDSA P-256 signing keys in the Microsoft Platform Crypto Provider (the TPM).
/// The key's ACL grants everything to Administrators only.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TpmKeys
{
    private const string AdminsOnlySddl = "O:BAG:SYD:(A;;GA;;;BA)";
    private const int DaclSecurityInformation = 4;
    private static readonly CngProvider Provider = CngProvider.MicrosoftPlatformCryptoProvider;

    public static ECDsa Create(string name)
    {
        var sd = new RawSecurityDescriptor(AdminsOnlySddl);
        var acl = new byte[sd.BinaryLength];
        sd.GetBinaryForm(acl, 0);

        var parameters = new CngKeyCreationParameters
        {
            Provider = Provider,
            KeyUsage = CngKeyUsages.Signing,
            KeyCreationOptions = CngKeyCreationOptions.MachineKey,
            ExportPolicy = CngExportPolicies.None,
        };
        // "Security Descr" must carry DACL_SECURITY_INFORMATION (4); with None the provider returns access denied.
        parameters.Parameters.Add(new CngProperty("Security Descr", acl, (CngPropertyOptions)DaclSecurityInformation));

        var key = CngKey.Create(CngAlgorithm.ECDsaP256, name, parameters);
        return new ECDsaCng(key);
    }

    public static ECDsa GetOrCreate(string name)
    {
        if (CngKey.Exists(name, Provider, CngKeyOpenOptions.MachineKey))
            return new ECDsaCng(CngKey.Open(name, Provider, CngKeyOpenOptions.MachineKey));

        return Create(name);
    }

    public static void Delete(string name)
    {
        if (!CngKey.Exists(name, Provider, CngKeyOpenOptions.MachineKey))
            return;

        using var key = CngKey.Open(name, Provider, CngKeyOpenOptions.MachineKey);
        key.Delete();
    }
}
