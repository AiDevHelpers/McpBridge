using AiDevHelpers.Common.Contracts;

namespace AiDevHelpers.ScriptHelper.Core;

public sealed record BrokerRequest(string ProjectRoot, string Script, IReadOnlyList<string> Args, string RequesterSid = "");

public sealed record BrokerResponse(bool Ok, string Reason, int? ExitCode, string Stdout, string Stderr);

/// <summary>
/// Decides and runs one request. The script that runs is a staged copy of the approved file set,
/// and the approval is checked against that copy, so what runs is exactly what was approved.
/// Every decision is audited.
/// </summary>
public sealed class RequestHandler
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(5);

    private readonly ProjectRegistry _registry;
    private readonly IApprovalSigner _signer;
    private readonly VersionCounter _versions;
    private readonly ScriptHelperConfig _config;
    private readonly IPathInspector _inspector;
    private readonly IInterpreterInfo _interpreter;
    private readonly IScriptRunner _runner;
    private readonly string _stagingRoot;
    private readonly Action<string> _audit;

    public RequestHandler(
        ProjectRegistry registry,
        IApprovalSigner signer,
        VersionCounter versions,
        ScriptHelperConfig config,
        IPathInspector inspector,
        IInterpreterInfo interpreter,
        IScriptRunner runner,
        string stagingRoot,
        Action<string> audit)
    {
        _registry = registry;
        _signer = signer;
        _versions = versions;
        _config = config;
        _inspector = inspector;
        _interpreter = interpreter;
        _runner = runner;
        _stagingRoot = stagingRoot;
        _audit = audit;
    }

    public BrokerResponse Handle(BrokerRequest request)
    {
        var response = Decide(request, out var stageDir);
        if (stageDir is not null && Directory.Exists(stageDir))
        {
            Directory.Delete(stageDir, recursive: true);
        }

        return response;
    }

    private BrokerResponse Decide(BrokerRequest request, out string? stageDir)
    {
        stageDir = null;

        var project = _registry.Find(request.ProjectRoot);
        if (project is null)
            return Deny(request, "project is not registered");

        // Only the user the project was registered to may run its scripts. An empty requester
        // or a project with no recorded owner is refused.
        if (string.IsNullOrWhiteSpace(request.RequesterSid)
            || !string.Equals(project.OwnerSid, request.RequesterSid, StringComparison.OrdinalIgnoreCase))
        {
            return Deny(request, "requester is not the project owner");
        }

        var manifestPath = Path.Combine(project.Root, ".elevated.json");
        byte[] manifestBytes;
        try
        {
            manifestBytes = File.ReadAllBytes(manifestPath);
        }
        catch (IOException)
        {
            return Deny(request, ".elevated.json cannot be read");
        }

        var manifest = ManifestParser.Parse(System.Text.Encoding.UTF8.GetString(manifestBytes));
        if (!manifest.Ok)
            return Deny(request, manifest.Reason);

        var resolved = PathRules.ResolveInsideRoot(project.Root, request.Script);
        if (!resolved.Ok)
            return Deny(request, $"'{request.Script}': {resolved.Reason}");

        var rel = Path.GetRelativePath(project.Root, resolved.FullPath).Replace('\\', '/');
        var key = ApprovalStore.NormalizeScriptPath(rel);
        if (key is null)
            return Deny(request, $"'{request.Script}' is not a valid relative path");

        var allow = Allowlist.Evaluate(manifest.Entries, rel, request.Args);
        if (!allow.Allowed)
            return Deny(request, allow.Reason);

        // Stage the whole approved file set, then check the approval against the staged bytes.
        var source = FingerprintBuilder.Build(project.Root, rel, _interpreter, _inspector);
        if (!source.Ok)
            return Deny(request, source.Reason);

        var stage = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
        stageDir = stage;
        Directory.CreateDirectory(stage);
        foreach (var file in source.Files)
        {
            var target = Path.Combine(stage, file.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(project.Root, file.Replace('/', '\\')), target, overwrite: false);
        }

        var staged = FingerprintBuilder.Build(stage, rel, _interpreter, _inspector);
        if (!staged.Ok)
            return Deny(request, staged.Reason);

        var allowHash = Fingerprint.Sha256Hex(manifestBytes);
        var decision = ApprovalStore.Authorize(
            _config.ApprovalsPath(project.Id),
            _signer,
            project.Id,
            project.Root,
            _versions.Get(project.Id),
            rel,
            staged.Fingerprint,
            allowHash);
        if (!decision.Allowed)
            return Deny(request, decision.Reason);

        var stagedScript = Path.Combine(stage, rel.Replace('/', '\\'));
        var run = _runner.Run(stagedScript, request.Args, RunTimeout);
        _audit($"allowed {project.Id} {rel} exit={run.ExitCode}");
        return new BrokerResponse(true, "ran", run.ExitCode, run.Stdout, run.Stderr);
    }

    private BrokerResponse Deny(BrokerRequest request, string reason)
    {
        _audit($"denied {request.ProjectRoot} {request.Script}: {reason}");
        return new BrokerResponse(false, reason, null, string.Empty, string.Empty);
    }
}
