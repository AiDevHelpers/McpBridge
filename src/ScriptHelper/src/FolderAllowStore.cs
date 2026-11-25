using System.Text;
using System.Text.Json;
using AiDevHelpers.Common.Contracts;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// One folder grant: a glob of full paths whose scripts may run elevated without per-script approval. DefinitionHash is the hash of the
/// glob as it was approved (the settings hash for this grant, since no repo settings file covers a folder). Version counts the grants
/// for this glob, so an older copy of the file is a replay. The signature covers every field, so editing any of them revokes the grant.
/// </summary>
public sealed record FolderAllowApproval(string Glob, string DefinitionHash, long Version, string Signature);

/// <summary>The signed folder grants, one file per glob, kept in the admin-only data root.</summary>
public static class FolderAllowStore
{
    private const string PayloadTag = "ScriptHelper.FolderAllow.v1";
    private const long MaxBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefinitionHash(string glob) =>
        Fingerprint.Sha256Hex(Encoding.UTF8.GetBytes(FolderGlob.Normalize(glob)));

    /// <summary>The replay counter key for a glob. Its hash is alphanumeric, so the counter file name is safe.</summary>
    public static string VersionKey(string glob) => "folder-" + DefinitionHash(glob);

    /// <summary>The grant file for a glob. The file name is the definition hash, so a glob has one grant.</summary>
    public static string PathFor(string directory, string glob) => Path.Combine(directory, DefinitionHash(glob) + ".json");

    /// <summary>Signs and writes atomically (temp file, then replace), then moves the replay counter for the glob forward.</summary>
    public static void Save(string directory, string glob, IApprovalSigner signer, VersionCounter versions)
    {
        var normalized = FolderGlob.Normalize(glob);
        var key = VersionKey(normalized);
        var version = versions.Get(key) + 1;
        var unsigned = new FolderAllowApproval(normalized, DefinitionHash(normalized), version, "");
        var signed = unsigned with { Signature = signer.Sign(Payload(unsigned)) };

        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, Path.GetRandomFileName());
        File.WriteAllText(temp, JsonSerializer.Serialize(signed, Options));
        File.Move(temp, PathFor(directory, normalized), overwrite: true);
        versions.Bump(key, version);
    }

    /// <summary>The globs with a valid, unchanged, current grant. Unreadable, altered, forged or replayed files grant nothing.</summary>
    public static IReadOnlyList<string> ActiveGlobs(string directory, IApprovalSigner signer, VersionCounter versions)
    {
        var globs = new List<string>();
        if (!Directory.Exists(directory))
            return globs;

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (TryLoad(file, signer, versions, out var doc))
                globs.Add(doc!.Glob);
        }

        return globs;
    }

    /// <summary>True when one of the globs covers the full path.</summary>
    public static bool Covers(IReadOnlyList<string> globs, string fullPath) =>
        globs.Any(g => FolderGlob.IsMatch(g, fullPath));

    /// <summary>True when an active grant in the directory covers the full path.</summary>
    public static bool Allows(string directory, IApprovalSigner signer, VersionCounter versions, string fullPath) =>
        Covers(ActiveGlobs(directory, signer, versions), fullPath);

    private static bool TryLoad(string file, IApprovalSigner signer, VersionCounter versions, out FolderAllowApproval? doc)
    {
        doc = null;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length > MaxBytes)
                return false;

            var bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
            var parsed = JsonSerializer.Deserialize<FolderAllowApproval>(bytes);
            if (parsed?.Glob is null || parsed.DefinitionHash is null || parsed.Signature is null)
                return false;

            if (!FolderGlob.IsAbsolute(parsed.Glob))
                return false;

            if (!string.Equals(parsed.DefinitionHash, DefinitionHash(parsed.Glob), StringComparison.Ordinal))
                return false;

            if (!signer.Verify(Payload(parsed with { Signature = "" }), parsed.Signature))
                return false;

            if (parsed.Version < versions.Get(VersionKey(parsed.Glob)))
                return false;

            doc = parsed;
            return true;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static byte[] Payload(FolderAllowApproval a)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        Fingerprint.WriteField(w, Encoding.UTF8.GetBytes(PayloadTag));
        Fingerprint.WriteField(w, Encoding.UTF8.GetBytes(a.Glob));
        Fingerprint.WriteField(w, Encoding.UTF8.GetBytes(a.DefinitionHash));
        w.Write(a.Version);
        w.Flush();
        return ms.ToArray();
    }
}
