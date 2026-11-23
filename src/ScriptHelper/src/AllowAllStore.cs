using System.Text;
using System.Text.Json;
using AiDevHelpers.Common.Contracts;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// One project's allow-all approval. It binds the registered project id, the checkout root, a version (replay) and the hash of the
/// repo's .aidevtools/settings.json, so editing that file, copying the approval to another checkout, or replaying an old one is refused.
/// </summary>
public sealed record AllowAllApproval(string ProjectId, string ProjectRoot, long Version, string SettingsHash, string Signature);

/// <summary>The signed allow-all approvals, kept in the admin-only data root, never in the repo.</summary>
public static class AllowAllStore
{
    private const string PayloadTag = "ScriptHelper.AllowAll.v1";
    private const long MaxBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Signs and writes atomically (temp file, then replace).</summary>
    public static void Save(string path, AllowAllApproval approval, IApprovalSigner signer)
    {
        var unsigned = approval with { ProjectRoot = NormalizeRoot(approval.ProjectRoot), Signature = "" };
        var signed = unsigned with { Signature = signer.Sign(Payload(unsigned)) };

        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, Path.GetRandomFileName());
        File.WriteAllText(temp, JsonSerializer.Serialize(signed, Options));
        File.Move(temp, path, overwrite: true);
    }

    public static Decision Check(
        string path,
        IApprovalSigner signer,
        string projectId,
        string projectRoot,
        long lastSeenVersion,
        string settingsHash)
    {
        AllowAllApproval? doc;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length > MaxBytes)
                return Deny("allow-all approval is too large");

            var bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
            doc = JsonSerializer.Deserialize<AllowAllApproval>(bytes);
        }
        catch (FileNotFoundException) { return Deny("allow-all is not approved for this project"); }
        catch (DirectoryNotFoundException) { return Deny("allow-all is not approved for this project"); }
        catch (JsonException) { return Deny("allow-all approval is malformed"); }
        catch (IOException) { return Deny("allow-all approval cannot be read"); }
        catch (UnauthorizedAccessException) { return Deny("allow-all approval cannot be read"); }

        if (doc is null || doc.ProjectId is null || doc.ProjectRoot is null || doc.SettingsHash is null || doc.Signature is null)
            return Deny("allow-all approval is malformed");

        if (!signer.Verify(Payload(doc with { ProjectRoot = NormalizeRoot(doc.ProjectRoot) }), doc.Signature))
            return Deny("allow-all approval signature is invalid");

        if (!string.Equals(doc.ProjectId, projectId, StringComparison.Ordinal))
            return Deny("allow-all approval belongs to another project");

        if (!string.Equals(NormalizeRoot(doc.ProjectRoot), NormalizeRoot(projectRoot), StringComparison.OrdinalIgnoreCase))
            return Deny("allow-all approval belongs to another checkout");

        if (doc.Version < lastSeenVersion)
            return Deny("replay: allow-all approval version is older than the last seen version");

        if (!string.Equals(doc.SettingsHash, settingsHash, StringComparison.Ordinal))
            return Deny("allow-all settings changed; approve again");

        return new Decision(true, "allow-all approved and unchanged");
    }

    private static string NormalizeRoot(string root) => Path.GetFullPath(root).TrimEnd('\\');

    private static byte[] Payload(AllowAllApproval a)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        Fingerprint.WriteField(w, Encoding.UTF8.GetBytes(PayloadTag));
        Fingerprint.WriteField(w, Encoding.UTF8.GetBytes(a.ProjectId));
        Fingerprint.WriteField(w, Encoding.UTF8.GetBytes(a.ProjectRoot));
        w.Write(a.Version);
        Fingerprint.WriteField(w, Encoding.UTF8.GetBytes(a.SettingsHash));
        w.Flush();
        return ms.ToArray();
    }

    private static Decision Deny(string reason) => new(false, reason);
}
