using System.Text;
using AiDevHelpers.Common;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>Installs the Script Helper tools: the installer script is placed and approved, never run here.</summary>
public sealed class ToolCommands(SetupDeps deps) : SetupStepBase(deps)
{
    public SetupResult InstallTools()
    {
        if (!_elevation.IsElevated())
            return Fail("setup needs an elevated shell");

        var root = InstallToolFiles.ToolsRoot(_config);
        try
        {
            _ensureDirectory(root);
            InstallToolFiles.Write(root);
            InstallWorkloadFiles.Write(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"the tools folder could not be written: {ex.Message}");
        }

        var registered = new RegistrationCommands(Deps).Register(root);
        if (!registered.Ok)
            return registered;

        var approvedTools = new ApprovalCommands(Deps).Approve(root, InstallToolFiles.ScriptName);
        return approvedTools.Ok ? new ApprovalCommands(Deps).Approve(root, InstallWorkloadFiles.ScriptName) : approvedTools;
    }
}

