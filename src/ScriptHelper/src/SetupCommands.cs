using AiDevHelpers.Common;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// The elevated setup steps, as one facade over the command groups: registration, tools, allows and approvals. Each group has its own
/// class and its own job; this type only wires them and forwards the calls the interfaces expose.
/// </summary>
public sealed class SetupCommands : ISetupCommands, IVerifyCommands, IToolCommands, IAllowCommands
{
    private readonly ScriptHelperConfig _config;
    private readonly RegistrationCommands _registration;
    private readonly ToolCommands _tools;
    private readonly AllowCommands _allow;
    private readonly ApprovalCommands _approval;

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
        var deps = new SetupDeps(config, elevation, volumes, inspector, interpreter, signer, confirmer, userProfileRoot, ensureDirectory ?? (p => Directory.CreateDirectory(p)));
        _registration = new RegistrationCommands(deps);
        _tools = new ToolCommands(deps);
        _allow = new AllowCommands(deps);
        _approval = new ApprovalCommands(deps);
        Versions = new VersionCounter(config.VersionsDirectory);
    }

    public VersionCounter Versions { get; }

    public string ApprovalsPath(string projectId) => _config.ApprovalsPath(projectId);

    public SetupResult Register(string root) => _registration.Register(root);

    public SetupResult PruneStale(string root) => _registration.PruneStale(root);

    public SetupResult InstallTools() => _tools.InstallTools();

    public SetupResult AllowScript(string root, string relativeScript, IReadOnlyList<string> args) => _allow.AllowScript(root, relativeScript, args);

    public SetupResult AllowFolder(string glob) => _allow.AllowFolder(glob);

    public SetupResult AllowAll(string root) => _allow.AllowAll(root);

    public SetupResult AllowAllApproved(string root, string approvedHash) => _allow.AllowAllApproved(root, approvedHash);

    public SetupResult Approve(string root, string relativeScript) => _approval.Approve(root, relativeScript);

    public VerifyReport Verify(string root) => _approval.Verify(root);

    public ScanReport Scan(string root) => _approval.Scan(root);

    public IReadOnlyList<SetupResult> AddToAllowlist(string root, Func<string, bool> confirm) => _approval.AddToAllowlist(root, confirm);

    public IReadOnlyList<SetupResult> ApproveUnsigned(string root, Func<string, bool> confirm) => _approval.ApproveUnsigned(root, confirm);

    public ApprovalPreview PrepareApproval(string root, string relativeScript) => _approval.PrepareApproval(root, relativeScript);

    public SetupResult ConfirmApproval(string root, string relativeScript, string fingerprint, string allowlistHash) =>
        _approval.ConfirmApproval(root, relativeScript, fingerprint, allowlistHash);
}
