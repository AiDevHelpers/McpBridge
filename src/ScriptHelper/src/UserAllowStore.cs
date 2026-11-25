using System.Text;
using System.Text.Json;
using AiDevHelpers.Common.Contracts;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// One script this user allowed for a project, with the exact arguments and the fingerprint of the script as it was shown. The entry is
/// the approval itself: the broker runs the script only while its staged bytes still hash to this fingerprint.
/// </summary>
public sealed record UserAllowEntry(string Path, IReadOnlyList<string> Args, string Fingerprint);

/// <summary>
/// One project's user allows. The signature covers every field, so a copied file is bound to ProjectId and ProjectRoot, and a replayed
/// one to Version (its own counter, apart from the approvals counter).
/// </summary>
public sealed record UserAllowDocument(string ProjectId, string ProjectRoot, long Version, IReadOnlyList<UserAllowEntry> Entries, string Signature);

/// <summary>
/// Scripts a user allowed for a repo without editing the repo, kept signed in the admin-only data root. The repo's
/// <c>.elevated.json</c> says what the team allows; this says what this user allowed on this machine. Only the elevated setup step
/// writes it, after a console yes. The broker consults it when the repo manifest does not allow a script.
/// </summary>
public static class UserAllowStore
{
    private const string PayloadTag = "ScriptHelper.UserAllow.v1";
    private const long MaxBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>The key under which the replay counter for a project's user allows is kept.</summary>
    public static string VersionKey(string projectId) => projectId + "-ua";

    public static void Save(string path, UserAllowDocument doc, IApprovalSigner signer)
    {
        var unsigned = doc with { ProjectRoot = NormalizeRoot(doc.ProjectRoot), Entries = Normalize(doc.Entries), Signature = "" };
        var signed = unsigned with { Signature = signer.Sign(Payload(unsigned)) };

        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = System.IO.Path.Combine(dir, System.IO.Path.GetRandomFileName());
        File.WriteAllText(temp, JsonSerializer.Serialize(signed, Options));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Reads and verifies the file. A missing file is Ok with no document.</summary>
    public static (bool Ok, UserAllowDocument? Document, string Reason) Load(string path, IApprovalSigner signer)
    {
        UserAllowDocument? doc;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length > MaxBytes)
                return (false, null, "user allow file is too large");

            var bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
            doc = JsonSerializer.Deserialize<UserAllowDocument>(bytes);
        }
        catch (FileNotFoundException) { return (true, null, "no user allows yet"); }
        catch (DirectoryNotFoundException) { return (true, null, "no user allows yet"); }
        catch (JsonException) { return (false, null, "user allow file is malformed"); }
        catch (IOException) { return (false, null, "user allow file cannot be read"); }
        catch (UnauthorizedAccessException) { return (false, null, "user allow file cannot be read"); }

        if (doc is null || doc.ProjectId is null || doc.ProjectRoot is null || doc.Entries is null || doc.Signature is null
            || doc.Entries.Any(e => e is null || e.Path is null || e.Args is null || e.Fingerprint is null))
        {
            return (false, null, "user allow file is malformed");
        }

        var normalized = doc with { ProjectRoot = NormalizeRoot(doc.ProjectRoot), Entries = Normalize(doc.Entries) };
        return signer.Verify(Payload(normalized with { Signature = "" }), doc.Signature)
            ? (true, normalized, "user allows verified")
            : (false, null, "user allow signature is invalid");
    }

    /// <summary>The verified document for this project and checkout, or null with the reason it cannot be used.</summary>
    public static UserAllowDocument? Usable(string path, IApprovalSigner signer, string projectId, string projectRoot, long lastSeenVersion, out string reason)
    {
        var loaded = Load(path, signer);
        reason = loaded.Reason;
        if (loaded.Document is null)
            return null;

        var doc = loaded.Document;
        if (!string.Equals(doc.ProjectId, projectId, StringComparison.Ordinal))
            reason = "user allows belong to another project";
        else if (!string.Equals(doc.ProjectRoot, NormalizeRoot(projectRoot), StringComparison.OrdinalIgnoreCase))
            reason = "user allows belong to another checkout";
        else if (doc.Version < lastSeenVersion)
            reason = "replay: user allows are older than the last seen version";
        else
            return doc;

        return null;
    }

    /// <summary>True when an entry names this script with exactly these arguments, whatever its fingerprint.</summary>
    public static bool HasEntry(UserAllowDocument? doc, string relativePath, IReadOnlyList<string> args) =>
        doc is not null && doc.Entries.Any(e => PathEquals(e.Path, relativePath) && e.Args.SequenceEqual(args, StringComparer.Ordinal));

    /// <summary>Allowed only when an entry names this script and these exact arguments with this exact fingerprint.</summary>
    public static Decision Authorize(UserAllowDocument? doc, string relativePath, IReadOnlyList<string> args, string fingerprint)
    {
        if (doc is null)
            return new Decision(false, "script is not allowed by the repo or by this user");

        var entry = doc.Entries.FirstOrDefault(e => PathEquals(e.Path, relativePath) && e.Args.SequenceEqual(args, StringComparer.Ordinal));
        if (entry is null)
            return new Decision(false, "script is not allowed by the repo or by this user");

        return string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal)
            ? new Decision(true, "allowed by this user's allow")
            : new Decision(false, "the script changed since this user allowed it; allow it again");
    }

    /// <summary>The document with this entry added (replacing an entry for the same path and arguments).</summary>
    public static UserAllowDocument With(UserAllowDocument? current, string projectId, string projectRoot, long version, UserAllowEntry entry)
    {
        var kept = (current?.Entries ?? [])
            .Where(e => !(PathEquals(e.Path, entry.Path) && e.Args.SequenceEqual(entry.Args, StringComparer.Ordinal)))
            .ToList();
        kept.Add(entry);
        return new UserAllowDocument(projectId, projectRoot, version, kept, string.Empty);
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<UserAllowEntry> Normalize(IReadOnlyList<UserAllowEntry> entries) =>
        [.. entries.Select(e => e with { Path = e.Path.Replace('\\', '/') }).OrderBy(e => e.Path, StringComparer.Ordinal)];

    private static string NormalizeRoot(string root) => System.IO.Path.GetFullPath(root).TrimEnd('\\');

    private static byte[] Payload(UserAllowDocument d)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        Fingerprint.WriteField(w, Encoding.UTF8.GetBytes(PayloadTag));
        Fingerprint.WriteField(w, Encoding.UTF8.GetBytes(d.ProjectId));
        Fingerprint.WriteField(w, Encoding.UTF8.GetBytes(d.ProjectRoot));
        w.Write(d.Version);
        w.Write(d.Entries.Count);
        foreach (var e in d.Entries)
        {
            Fingerprint.WriteField(w, Encoding.UTF8.GetBytes(e.Path));
            w.Write(e.Args.Count);
            foreach (var a in e.Args)
                Fingerprint.WriteField(w, Encoding.UTF8.GetBytes(a));

            Fingerprint.WriteField(w, Encoding.UTF8.GetBytes(e.Fingerprint));
        }

        w.Flush();
        return ms.ToArray();
    }
}
