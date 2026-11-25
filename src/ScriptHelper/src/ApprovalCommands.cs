using System.Text;
using AiDevHelpers.Common;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>Shows a script's fingerprint and approves it after a person answers. Verify, scan and unsigned approvals live here too.</summary>
public sealed class ApprovalCommands(SetupDeps deps) : SetupStepBase(deps)
{
    public string ApprovalsPath(string projectId) => _config.ApprovalsPath(projectId);
    public SetupResult Approve(string root, string relativeScript)
    {
        var analysis = Analyze(root, relativeScript);
        if (!analysis.Ok)
            return Fail(analysis.Reason);

        var prompt = BuildPrompt(analysis);
        if (!_confirmer.Confirm(prompt))
            return Fail("approval declined");

        return Write(analysis);
    }

    /// <summary>
    /// Approves allow-all for a registered project whose repo settings request it: any script inside the repo may then run
    /// elevated without per-script approval. Two separate confirmations are asked from the elevated console, the second repeating
    /// the repo path; nothing is written unless both are yes. The signed approval goes to the admin-only data root, bound to the
    /// project, the checkout and the hash of the settings file, and this user's gitignored .aidevhelpers/user.json gets a pointer to it.
    /// </summary>
    public VerifyReport Verify(string root)
    {
        if (!_elevation.IsElevated())
            return VerifyReport.Failed("setup needs an elevated shell");

        if (!TryRegisteredProject(root, out var project))
            return VerifyReport.Failed(NotRegisteredReason);

        return AllowlistVerifier.Run(_config, _signer, Versions, _interpreter, _inspector, project);
    }

    /// <summary>Scans the repo's launch and debug configuration for scripts. Reads only.</summary>
    public ScanReport Scan(string root)
    {
        if (!_elevation.IsElevated())
            return ScanReport.Failed("setup needs an elevated shell");

        if (!TryRegisteredProject(root, out var project))
            return ScanReport.Failed(NotRegisteredReason);

        string text;
        try
        {
            text = File.ReadAllText(Path.Combine(project.Root, ManifestName));
        }
        catch (IOException)
        {
            return ScanReport.Failed($"{ManifestName} cannot be read");
        }

        var manifest = ManifestParser.Parse(text);
        return manifest.Ok
            ? new ScanReport(true, "scanned", LaunchScriptScanner.Scan(project.Root, manifest.Entries))
            : ScanReport.Failed(manifest.Reason);
    }

    /// <summary>
    /// Adds each script the confirm function accepts to the project's .elevated.json. The edit changes the allowlist, so every
    /// script needs approving again afterwards (setup verify). Scripts already listed are never offered.
    /// </summary>
    public IReadOnlyList<SetupResult> AddToAllowlist(string root, Func<string, bool> confirm)
    {
        var report = Scan(root);
        if (!report.Ok)
            return [Fail(report.Reason)];

        var manifestPath = Path.Combine(_registry.Find(root)!.Root, ManifestName);
        var results = new List<SetupResult>();
        foreach (var script in report.Scripts.Where(s => !s.Listed))
        {
            if (!confirm(script.Path))
                continue;

            File.WriteAllText(manifestPath, ManifestEditor.AddScript(File.ReadAllText(manifestPath), script.Path));
            results.Add(new SetupResult(true, $"added '{script.Path}' to {ManifestName}"));
        }

        return results;
    }

    /// <summary>
    /// Approves each listed script that is not approved, asking the confirm function for each one first (the console asks the
    /// person). Scripts the person declines stay unsigned. Returns one result per approval attempted.
    /// </summary>
    public IReadOnlyList<SetupResult> ApproveUnsigned(string root, Func<string, bool> confirm)
    {
        var report = Verify(root);
        if (!report.Ok)
            return [Fail(report.Reason)];

        var results = new List<SetupResult>();
        foreach (var script in report.Scripts.Where(s => s.Status is not ApprovalStatus.Approved and not ApprovalStatus.Invalid))
        {
            if (!confirm(script.Path))
                continue;

            var analysis = Analyze(root, script.Path);
            results.Add(analysis.Ok ? Write(analysis) : Fail(analysis.Reason));
        }

        return results;
    }

    public ApprovalPreview PrepareApproval(string root, string relativeScript)
    {
        var analysis = Analyze(root, relativeScript);
        if (!analysis.Ok)
            return new ApprovalPreview(false, analysis.Reason, string.Empty, string.Empty, string.Empty);

        return new ApprovalPreview(true, "prepared", BuildPrompt(analysis), analysis.Fingerprint.Fingerprint, analysis.AllowlistHash);
    }

    public SetupResult ConfirmApproval(string root, string relativeScript, string fingerprint, string allowlistHash)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || string.IsNullOrWhiteSpace(allowlistHash))
            return Fail("confirm needs both the fingerprint and the allowlist hash that were shown");

        var analysis = Analyze(root, relativeScript);
        if (!analysis.Ok)
            return Fail(analysis.Reason);

        // The values must match what was shown; otherwise the person approved something else.
        if (!string.Equals(analysis.Fingerprint.Fingerprint, fingerprint, StringComparison.Ordinal)
            || !string.Equals(analysis.AllowlistHash, allowlistHash, StringComparison.Ordinal))
        {
            return Fail("the script or allowlist changed since it was shown; prepare again and show the new values");
        }

        return Write(analysis);
    }

    /// <summary>Everything Approve needs before asking a person. Writes nothing.</summary>
    private Analysis Analyze(string root, string relativeScript)
    {
        if (!_elevation.IsElevated())
            return Analysis.Failed("setup needs an elevated shell");

        if (!TryRegisteredProject(root, out var project))
            return Analysis.Failed(NotRegisteredReason);

        byte[] manifestBytes;
        try
        {
            manifestBytes = File.ReadAllBytes(Path.Combine(project.Root, ManifestName));
        }
        catch (FileNotFoundException)
        {
            return Analysis.Failed($"{ManifestName} not found in the project root");
        }
        catch (DirectoryNotFoundException)
        {
            return Analysis.Failed($"{ManifestName} not found in the project root");
        }
        catch (IOException)
        {
            return Analysis.Failed($"{ManifestName} cannot be read");
        }

        var manifest = ManifestParser.Parse(Encoding.UTF8.GetString(manifestBytes));
        if (!manifest.Ok)
            return Analysis.Failed(manifest.Reason);

        var resolved = PathRules.ResolveInsideRoot(project.Root, relativeScript);
        if (!resolved.Ok)
            return Analysis.Failed($"'{relativeScript}': {resolved.Reason}");

        var rel = Path.GetRelativePath(project.Root, resolved.FullPath).Replace('\\', '/');
        var key = ApprovalStore.NormalizeScriptPath(rel);
        if (key is null)
            return Analysis.Failed($"'{relativeScript}' is not a valid relative path");

        if (!Allowlist.IsPathListed(manifest.Entries, rel))
            return Analysis.Failed($"script is not listed in {ManifestName}");

        var fingerprint = FingerprintBuilder.Build(project.Root, rel, _interpreter, _inspector);
        if (!fingerprint.Ok)
            return Analysis.Failed(fingerprint.Reason);

        var allowlistHash = Fingerprint.Sha256Hex(manifestBytes);

        _ensureDirectory(_config.ApprovalsDirectory);
        _ensureDirectory(_config.VersionsDirectory);

        var approvalsPath = ApprovalsPath(project.Id);
        var load = ApprovalStore.TryLoad(approvalsPath, _signer);
        if (!load.Ok)
            return Analysis.Failed($"existing approvals are not trusted ({load.Reason}); refusing to overwrite them");

        var existing = load.Document;
        if (existing is not null && !string.Equals(existing.ProjectId, project.Id, StringComparison.Ordinal))
            return Analysis.Failed("existing approvals belong to another project; refusing to overwrite them");

        return new Analysis(true, string.Empty, project, rel, key, fingerprint, allowlistHash, existing, approvalsPath);
    }

    /// <summary>Writes the approval the analysis describes. Only called after a person (or a matching confirm).</summary>
    private SetupResult Write(Analysis a)
    {
        var version = Math.Max(Versions.Get(a.Project.Id), a.Existing?.Version ?? 0) + 1;
        var scripts = (a.Existing?.Scripts ?? Array.Empty<ApprovalRecord>())
            .Where(r => r.Path != a.Key)
            .Append(new ApprovalRecord(a.Key, a.Fingerprint.Fingerprint))
            .ToList();

        ApprovalStore.Save(
            a.ApprovalsPath,
            new ApprovalDocument(a.Project.Id, a.Project.Root, version, a.AllowlistHash, scripts, Signature: ""),
            _signer);
        Versions.Bump(a.Project.Id, version);

        return new SetupResult(true, $"approved '{a.Key}' (approvals version {version})", a.Project.Id);
    }

    private sealed record Analysis(
        bool Ok,
        string Reason,
        ProjectEntry Project,
        string Rel,
        string Key,
        FingerprintResult Fingerprint,
        string AllowlistHash,
        ApprovalDocument? Existing,
        string ApprovalsPath)
    {
        public static Analysis Failed(string reason) =>
            new(false, reason, null!, string.Empty, string.Empty, null!, string.Empty, null, string.Empty);
    }

    private static string BuildPrompt(Analysis a)
    {
        var root = a.Project.Root;
        var rel = a.Rel;
        var fingerprint = a.Fingerprint;
        var allowlistHash = a.AllowlistHash;
        var existing = a.Existing;

        // One labelled value per section, a blank line between sections, and the hashes on one
        // unbroken line each so they can be compared or searched.
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("Approve this script to run elevated?");
        sb.AppendLine();
        Section(sb, "Project", root);
        Section(sb, "Script", rel);
        Section(sb, $"Files pinned ({fingerprint.Files.Count})", fingerprint.Files.OrderBy(f => f, StringComparer.Ordinal).ToArray());
        Section(sb, "Fingerprint", fingerprint.Fingerprint);
        Section(sb, "Allowlist hash", allowlistHash);

        if (existing is not null && !string.Equals(existing.AllowlistHash, allowlistHash, StringComparison.Ordinal))
        {
            sb.AppendLine("WARNING: the allowlist changed since the last approval.");
            sb.AppendLine("    Previous hash:");
            sb.AppendLine($"    {existing.AllowlistHash}");
            sb.AppendLine("    Scripts approved earlier stay pinned by fingerprint under the new allowlist.");
            sb.AppendLine();
        }

        return sb.ToString();
    }

}

