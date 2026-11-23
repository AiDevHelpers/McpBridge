using System.Text;
using AiDevHelpers.Common;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// The elevated setup steps: register a project root, and approve a script for it.
/// Everything here needs an elevated shell. Approve shows the script, its fingerprint and the
/// allowlist hash, and writes nothing until a person confirms.
/// </summary>
public sealed class SetupCommands : ISetupCommands
{
    private const string ManifestName = ".elevated.json";

    private readonly ScriptHelperConfig _config;
    private readonly IElevationCheck _elevation;
    private readonly IPathInspector _inspector;
    private readonly IInterpreterInfo _interpreter;
    private readonly IApprovalSigner _signer;
    private readonly IConfirmer _confirmer;
    private readonly Action<string> _ensureDirectory;
    private readonly ProjectRegistry _registry;

    public SetupCommands(
        ScriptHelperConfig config,
        IElevationCheck elevation,
        IVolumeInfo volumes,
        IPathInspector inspector,
        IInterpreterInfo interpreter,
        IApprovalSigner signer,
        IConfirmer confirmer,
        string userProfileRoot,
        Action<string>? ensureDirectory = null)
    {
        _config = config;
        _elevation = elevation;
        _inspector = inspector;
        _interpreter = interpreter;
        _signer = signer;
        _confirmer = confirmer;
        _ensureDirectory = ensureDirectory ?? (p => Directory.CreateDirectory(p));

        _registry = new ProjectRegistry(
            config.RegistryPath, elevation, volumes, userProfileRoot, _ensureDirectory);
        Versions = new VersionCounter(config.VersionsDirectory);
    }

    public VersionCounter Versions { get; }

    public string ApprovalsPath(string projectId) => _config.ApprovalsPath(projectId);

    public SetupResult Register(string root)
    {
        var result = _registry.Register(root);
        if (!result.Ok)
            return result;

        // The owner's queue and results folders are created with owner-only ACLs, so the owner
        // can submit requests and read answers, and nobody else can.
        var project = _registry.Find(root);
        if (project is null)
            return Fail("project was registered but could not be read back");

        RequesterFolders.Ensure(_config, project.OwnerSid);
        return result;
    }

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
    /// project, the checkout and the hash of the settings file, and this user's gitignored .aidevtools/user.json gets a pointer to it.
    /// </summary>
    public SetupResult AllowAll(string root)
    {
        if (!_elevation.IsElevated())
            return Fail("setup needs an elevated shell");

        var project = _registry.Find(root);
        if (project is null)
            return Fail("project is not registered; run register first");

        var settingsPath = RepoSettingsFile.SettingsPath(project.Root);
        if (!SettingsRequestAllowAll(settingsPath))
            return Fail($"{RepoSettingsFile.FolderName}/{RepoSettingsFile.SettingsFileName} does not request allowAll");

        var hash = RepoSettingsFile.Hash(settingsPath);
        var repo = Path.GetFullPath(project.Root);

        if (!_confirmer.Confirm(
                $"Allow ALL scripts in {repo} to run elevated, without per-script approval?\n" +
                "Anyone who can change files in this repo, including a teammate's later commit, can then run code as administrator on this machine.\n" +
                $"Settings hash: {hash}"))
        {
            return Fail("allow-all declined");
        }

        if (!_confirmer.Confirm(
                $"Second confirmation. Really allow all scripts in {repo} to run elevated on this machine? " +
                "Editing the repo's settings file will revoke this until you approve again."))
        {
            return Fail("allow-all declined at the second confirmation");
        }

        _ensureDirectory(_config.AllowAllDirectory);
        _ensureDirectory(_config.VersionsDirectory);
        var version = Versions.Get(project.Id) + 1;
        AllowAllStore.Save(_config.AllowAllPath(project.Id), new AllowAllApproval(project.Id, project.Root, version, hash, ""), _signer);
        Versions.Bump(project.Id, version);

        WriteUserPointer(project, hash);
        return new SetupResult(true, $"allow-all approved for '{repo}' (version {version})", project.Id);
    }

    private static bool SettingsRequestAllowAll(string settingsPath)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("allowAll", out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // The gitignored per-user file only points at the approval; the approval itself lives in the admin store.
    private static void WriteUserPointer(ProjectEntry project, string hash)
    {
        var path = RepoSettingsFile.UserFilePath(project.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(
            new { allowAll = new { projectId = project.Id, settingsHash = hash } },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        RepoSettingsFile.EnsureUserFileGitIgnored(project.Root);
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

        var project = _registry.Find(root);
        if (project is null)
            return Analysis.Failed("project is not registered; run register first");

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

    private static void Section(StringBuilder sb, string label, params string[] values)
    {
        sb.AppendLine(label);
        foreach (var value in values)
        {
            sb.AppendLine($"    {value}");
        }

        sb.AppendLine();
    }

    private static SetupResult Fail(string reason) => new(false, reason);
}
