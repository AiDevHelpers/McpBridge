using System.Text;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// Checks every file a project's allowlist lists for a valid approval from this machine's admin store. Reads only. Shared by
/// the elevated setup command and the broker's read-only verify request.
/// </summary>
public static class AllowlistVerifier
{
    private const string ManifestName = ".elevated.json";
    private const int MaxFiles = 20000;
    private static readonly string[] SkippedFolders = [".git", ".vs", "bin", "obj", "node_modules"];

    public static VerifyReport Run(
        ScriptHelperConfig config,
        IApprovalSigner signer,
        VersionCounter versions,
        IInterpreterInfo interpreter,
        IPathInspector inspector,
        ProjectEntry project)
    {
        byte[] manifestBytes;
        try
        {
            manifestBytes = File.ReadAllBytes(Path.Combine(project.Root, ManifestName));
        }
        catch (IOException)
        {
            return VerifyReport.Failed($"{ManifestName} cannot be read");
        }

        var manifest = ManifestParser.Parse(Encoding.UTF8.GetString(manifestBytes));
        if (!manifest.Ok)
            return VerifyReport.Failed(manifest.Reason);

        var lastSeen = versions.Get(project.Id);
        var allowAll = AllowAllStore.IsActive(config, signer, project.Id, project.Root, lastSeen);
        var allowlistHash = Fingerprint.Sha256Hex(manifestBytes);
        var approvalsPath = config.ApprovalsPath(project.Id);
        var folderGlobs = FolderAllowStore.ActiveGlobs(config.FolderAllowDirectory, signer, versions);
        var scripts = new List<ScriptStatus>();

        foreach (var rel in RepoFiles(project.Root).Where(r => Allowlist.IsPathListed(manifest.Entries, r)).Order(StringComparer.Ordinal))
        {
            if (FolderAllowStore.Covers(folderGlobs, Path.GetFullPath(Path.Combine(project.Root, rel))))
            {
                scripts.Add(new ScriptStatus(rel, ApprovalStatus.Approved, "covered by a folder allow"));
                continue;
            }

            var fingerprint = FingerprintBuilder.Build(project.Root, rel, interpreter, inspector);
            if (!fingerprint.Ok)
            {
                scripts.Add(new ScriptStatus(rel, ApprovalStatus.Invalid, fingerprint.Reason));
                continue;
            }

            var decision = ApprovalStore.Authorize(approvalsPath, signer, project.Id, project.Root, lastSeen, rel, fingerprint.Fingerprint, allowlistHash);
            scripts.Add(new ScriptStatus(rel, decision.Allowed ? ApprovalStatus.Approved : StatusFor(decision.Reason), decision.Reason));
        }

        return new VerifyReport(true, "verified", allowAll, scripts);
    }

    private static ApprovalStatus StatusFor(string reason) => reason switch
    {
        _ when reason.Contains("script changed", StringComparison.Ordinal) => ApprovalStatus.Changed,
        _ when reason.Contains("allowlist changed", StringComparison.Ordinal) => ApprovalStatus.AllowlistChanged,
        _ when reason.Contains("not approved", StringComparison.Ordinal) || reason.Contains("missing", StringComparison.Ordinal) => ApprovalStatus.Unsigned,
        _ => ApprovalStatus.Invalid,
    };

    private static IEnumerable<string> RepoFiles(string root)
    {
        var count = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (!SkippedFolders.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                    pending.Push(sub);
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (++count > MaxFiles)
                    yield break;

                yield return Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            }
        }
    }
}
