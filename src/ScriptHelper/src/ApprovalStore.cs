using System.Text;
using System.Text.Json;

namespace AiDevHelpers.ScriptHelper.Core;

public sealed record ApprovalRecord(string Path, string Fingerprint);

/// <summary>
/// One project's approvals. The signature covers every field through an explicit
/// canonical payload, so a copied file is bound to ProjectId and ProjectRoot,
/// and a replayed file is bound to Version.
/// </summary>
public sealed record ApprovalDocument(
    string ProjectId,
    string ProjectRoot,
    long Version,
    string AllowlistHash,
    IReadOnlyList<ApprovalRecord> Scripts,
    string Signature);

/// <summary>
/// Ok with a null Document means no approvals file exists yet. Ok with a Document means the file
/// was read, parsed, normalized and its signature verified.
/// </summary>
public sealed record LoadResult(bool Ok, ApprovalDocument? Document, string Reason);

public static class ApprovalStore
{
    private const string PayloadTag = "ScriptHelper.Approvals.v1";
    private const long MaxBytes = 1024 * 1024;
    private const string Malformed = "approvals file is malformed";
    private const string Missing = "approvals file missing; approve the script first";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// Normalizes paths and roots, signs, and writes atomically (temp file, then replace),
    /// so a concurrent reader never sees a torn file.
    /// </summary>
    public static void Save(string path, ApprovalDocument doc, IApprovalSigner signer)
    {
        if (!TryNormalizeScripts(doc.Scripts, out var scripts))
            throw new ArgumentException("script paths must be valid and unique after normalization");

        var unsigned = doc with { ProjectRoot = NormalizeRoot(doc.ProjectRoot), Scripts = scripts, Signature = "" };
        var signed = unsigned with { Signature = signer.Sign(Payload(unsigned)) };

        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);

        var temp = Path.Combine(dir, Path.GetRandomFileName());
        File.WriteAllText(temp, JsonSerializer.Serialize(signed, Options));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Reads and verifies an approvals file without checking project, root, version or script.
    /// Used by approval, which must not overwrite a file it cannot trust.
    /// </summary>
    public static LoadResult TryLoad(string path, IApprovalSigner signer)
    {
        byte[] bytes;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length > MaxBytes)
                return Fail("approvals file is too large");

            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }
        catch (FileNotFoundException) { return new LoadResult(true, null, "no approvals yet"); }
        catch (DirectoryNotFoundException) { return new LoadResult(true, null, "no approvals yet"); }
        catch (IOException) { return Fail("approvals file cannot be read"); }
        catch (UnauthorizedAccessException) { return Fail("approvals file cannot be read"); }

        ApprovalDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ApprovalDocument>(bytes);
        }
        catch (JsonException)
        {
            return Fail(Malformed);
        }

        if (doc is null
            || string.IsNullOrWhiteSpace(doc.ProjectId)
            || string.IsNullOrWhiteSpace(doc.ProjectRoot)
            || doc.AllowlistHash is null
            || doc.Signature is null
            || doc.Scripts is null)
        {
            return Fail(Malformed);
        }

        if (!TryNormalizeScripts(doc.Scripts, out var scripts))
            return Fail(Malformed);

        ApprovalDocument normalized;
        try
        {
            normalized = doc with { ProjectRoot = NormalizeRoot(doc.ProjectRoot), Scripts = scripts };
        }
        catch (ArgumentException)
        {
            return Fail(Malformed);
        }
        catch (NotSupportedException)
        {
            return Fail(Malformed);
        }

        if (!signer.Verify(Payload(normalized), doc.Signature))
            return Fail("approvals signature is invalid");

        return new LoadResult(true, normalized, "loaded");
    }

    public static Decision Authorize(
        string path,
        IApprovalSigner signer,
        string expectedProjectId,
        string expectedProjectRoot,
        long lastSeenVersion,
        string scriptPath,
        string fingerprint,
        string allowlistHash)
    {
        var load = TryLoad(path, signer);
        if (!load.Ok)
            return Deny(load.Reason);

        var doc = load.Document;
        if (doc is null)
            return Deny(Missing);

        if (!string.Equals(doc.ProjectId, expectedProjectId, StringComparison.Ordinal))
            return Deny("approvals belong to another project");

        if (!string.Equals(doc.ProjectRoot, NormalizeRoot(expectedProjectRoot), StringComparison.OrdinalIgnoreCase))
            return Deny("approvals belong to another checkout");

        if (doc.Version < lastSeenVersion)
            return Deny("replay: approvals version is older than the last seen version");

        if (!string.Equals(doc.AllowlistHash, allowlistHash, StringComparison.Ordinal))
            return Deny("allowlist changed; re-approval needed");

        var key = NormalizeScriptPath(scriptPath);
        var record = key is null ? null : doc.Scripts.FirstOrDefault(r => r.Path == key);

        if (record is null)
            return Deny("script is not approved");

        if (!string.Equals(record.Fingerprint, fingerprint, StringComparison.Ordinal))
            return Deny("script changed; re-approval needed");

        return new Decision(true, "approved and unchanged");
    }

    /// <summary>
    /// Normalizes to forward slashes, lowercase, no '.' or '..' segments, and no trailing
    /// dots or spaces. Returns null for rooted or escaping paths. Case-insensitive matching
    /// is deliberate for Windows; reparse-point checks live in <see cref="FingerprintBuilder"/>.
    /// </summary>
    internal static string? NormalizeScriptPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path.Replace('/', '\\')))
            return null;

        var stack = new List<string>();

        foreach (var raw in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw == ".")
                continue;

            if (raw == "..")
            {
                if (stack.Count == 0)
                    return null;
                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            var segment = raw.TrimEnd(' ', '.');
            if (segment.Length == 0)
                return null;

            stack.Add(segment);
        }

        return stack.Count == 0 ? null : string.Join('/', stack).ToLowerInvariant();
    }

    private static string NormalizeRoot(string root) =>
        Path.GetFullPath(root).TrimEnd('\\');

    private static bool TryNormalizeScripts(IReadOnlyList<ApprovalRecord?> scripts, out IReadOnlyList<ApprovalRecord> result)
    {
        var list = new List<ApprovalRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var record in scripts)
        {
            if (record?.Path is null || record.Fingerprint is null)
            {
                result = Array.Empty<ApprovalRecord>();
                return false;
            }

            var key = NormalizeScriptPath(record.Path);
            if (key is null || !seen.Add(key))
            {
                result = Array.Empty<ApprovalRecord>();
                return false;
            }

            list.Add(new ApprovalRecord(key, record.Fingerprint));
        }

        result = list.OrderBy(r => r.Path, StringComparer.Ordinal).ToList();
        return true;
    }

    /// <summary>Canonical signed bytes: tag, then length-prefixed fields in a fixed order.</summary>
    private static byte[] Payload(ApprovalDocument d)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        Fingerprint.WriteField(w, Encoding.UTF8.GetBytes(PayloadTag));
        Fingerprint.WriteField(w, Encoding.UTF8.GetBytes(d.ProjectId));
        Fingerprint.WriteField(w, Encoding.UTF8.GetBytes(d.ProjectRoot));
        w.Write(d.Version);
        Fingerprint.WriteField(w, Encoding.UTF8.GetBytes(d.AllowlistHash));

        w.Write(d.Scripts.Count);
        foreach (var s in d.Scripts)
        {
            Fingerprint.WriteField(w, Encoding.UTF8.GetBytes(s.Path));
            Fingerprint.WriteField(w, Encoding.UTF8.GetBytes(s.Fingerprint));
        }

        w.Flush();
        return ms.ToArray();
    }

    private static LoadResult Fail(string reason) => new(false, null, reason);

    private static Decision Deny(string reason) => new(false, reason);
}
