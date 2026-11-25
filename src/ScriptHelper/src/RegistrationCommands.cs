using System.Text;
using AiDevHelpers.Common;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>Registers project roots and removes stale ones. Both need an elevated shell.</summary>
public sealed class RegistrationCommands(SetupDeps deps) : SetupStepBase(deps)
{
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

    /// <summary>
    /// Puts the installer script (<see cref="InstallToolFiles"/>) in the admin-only install folder, registers that folder as a project and
    /// approves the script. Approval shows the script and its fingerprint and writes nothing until the person confirms.
    public SetupResult PruneStale(string root) => _registry.Prune(root);
}

