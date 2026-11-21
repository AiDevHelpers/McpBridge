using System.Text;

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

    public SetupResult Register(string root) => _registry.Register(root);

    public SetupResult Approve(string root, string relativeScript)
    {
        if (!_elevation.IsElevated())
            return Fail("setup needs an elevated shell");

        var project = _registry.Find(root);
        if (project is null)
            return Fail("project is not registered; run register first");

        byte[] manifestBytes;
        try
        {
            manifestBytes = File.ReadAllBytes(Path.Combine(project.Root, ManifestName));
        }
        catch (FileNotFoundException)
        {
            return Fail($"{ManifestName} not found in the project root");
        }
        catch (DirectoryNotFoundException)
        {
            return Fail($"{ManifestName} not found in the project root");
        }
        catch (IOException)
        {
            return Fail($"{ManifestName} cannot be read");
        }

        var manifest = ManifestParser.Parse(Encoding.UTF8.GetString(manifestBytes));
        if (!manifest.Ok)
            return Fail(manifest.Reason);

        var resolved = PathRules.ResolveInsideRoot(project.Root, relativeScript);
        if (!resolved.Ok)
            return Fail($"'{relativeScript}': {resolved.Reason}");

        var rel = Path.GetRelativePath(project.Root, resolved.FullPath).Replace('\\', '/');
        var key = ApprovalStore.NormalizeScriptPath(rel);
        if (key is null)
            return Fail($"'{relativeScript}' is not a valid relative path");

        if (!Allowlist.IsPathListed(manifest.Entries, rel))
            return Fail($"script is not listed in {ManifestName}");

        var fingerprint = FingerprintBuilder.Build(project.Root, rel, _interpreter, _inspector);
        if (!fingerprint.Ok)
            return Fail(fingerprint.Reason);

        var allowlistHash = Fingerprint.Sha256Hex(manifestBytes);

        _ensureDirectory(_config.ApprovalsDirectory);
        _ensureDirectory(_config.VersionsDirectory);

        var approvalsPath = ApprovalsPath(project.Id);
        var load = ApprovalStore.TryLoad(approvalsPath, _signer);
        if (!load.Ok)
            return Fail($"existing approvals are not trusted ({load.Reason}); refusing to overwrite them");

        var existing = load.Document;
        if (existing is not null && !string.Equals(existing.ProjectId, project.Id, StringComparison.Ordinal))
            return Fail("existing approvals belong to another project; refusing to overwrite them");

        var prompt = BuildPrompt(project.Root, rel, fingerprint, allowlistHash, existing);
        if (!_confirmer.Confirm(prompt))
            return Fail("approval declined");

        var version = Math.Max(Versions.Get(project.Id), existing?.Version ?? 0) + 1;
        var scripts = (existing?.Scripts ?? Array.Empty<ApprovalRecord>())
            .Where(r => r.Path != key)
            .Append(new ApprovalRecord(key, fingerprint.Fingerprint))
            .ToList();

        ApprovalStore.Save(
            approvalsPath,
            new ApprovalDocument(project.Id, project.Root, version, allowlistHash, scripts, Signature: ""),
            _signer);
        Versions.Bump(project.Id, version);

        return new SetupResult(true, $"approved '{key}' (approvals version {version})", project.Id);
    }

    private static string BuildPrompt(
        string root, string rel, FingerprintResult fingerprint, string allowlistHash, ApprovalDocument? existing)
    {
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
