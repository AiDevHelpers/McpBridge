using System.Text;
using System.Text;
using AiDevHelpers.Common;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>The services every setup command group needs. Groups share no mutable state; the registry and version files are on disk.</summary>
public sealed record SetupDeps(
    ScriptHelperConfig Config,
    IElevationCheck Elevation,
    IVolumeInfo Volumes,
    IPathInspector Inspector,
    IInterpreterInfo Interpreter,
    IApprovalSigner Signer,
    IConfirmer Confirmer,
    string UserProfileRoot,
    Action<string> EnsureDirectory);

/// <summary>Base for the setup command groups: holds the shared services and the common failure helpers.</summary>
public abstract class SetupStepBase
{
    protected const string ManifestName = ".elevated.json";
    protected const string NotRegisteredReason = "project is not registered; run register first";

    protected readonly ScriptHelperConfig _config;
    protected readonly IElevationCheck _elevation;
    protected readonly IPathInspector _inspector;
    protected readonly IInterpreterInfo _interpreter;
    protected readonly IApprovalSigner _signer;
    protected readonly IConfirmer _confirmer;
    protected readonly Action<string> _ensureDirectory;
    protected readonly ProjectRegistry _registry;

    protected SetupStepBase(SetupDeps deps)
    {
        Deps = deps;
        _config = deps.Config;
        _elevation = deps.Elevation;
        _inspector = deps.Inspector;
        _interpreter = deps.Interpreter;
        _signer = deps.Signer;
        _confirmer = deps.Confirmer;
        _ensureDirectory = deps.EnsureDirectory ?? (p => Directory.CreateDirectory(p));
        _registry = new ProjectRegistry(deps.Config.RegistryPath, deps.Elevation, deps.Volumes, deps.UserProfileRoot, _ensureDirectory);
        Versions = new VersionCounter(deps.Config.VersionsDirectory);
    }

    protected SetupDeps Deps { get; }

    protected VersionCounter Versions { get; }

    protected bool TryRegisteredProject(string root, out ProjectEntry project)
    {
        project = _registry.Find(root)!;
        return project is not null;
    }

    protected static SetupResult Fail(string reason) => new(false, reason);

    // Renders the batched text of a setup section (shared by the approval prompt and the allow prompt).
    protected static void Section(StringBuilder sb, string label, params string[] values)
    {
        sb.AppendLine(label);
        foreach (var value in values)
        {
            sb.AppendLine($"    {value}");
        }

        sb.AppendLine();
    }
}

