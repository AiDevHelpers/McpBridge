using AiDevHelpers.ScriptHelper.Core;

namespace AiDevHelpers.ScriptHelper.Client;

/// <summary>The elevated setup wiring. Runs only in an elevated process; the elevation check is repeated here so it cannot be skipped.</summary>
internal static class SetupHost
{
    public static int Run(string[] setupArgs, bool allowNoTpm, bool confirmRegister = false)
    {
        var elevation = new WindowsElevationCheck();
        if (!elevation.IsElevated())
        {
            Console.Error.WriteLine("refused: setup needs an elevated shell");
            return 1;
        }

        // %ProgramData%\AiDevHelpers is shared with other components, so it stays an ordinary folder.
        // Only the script-helper subfolder is locked down to Administrators.
        var repository = ConfigRepository.FromEnvironment();
        var config = repository.ScriptHelper;
        Directory.CreateDirectory(repository.Root);
        AdminOnlyDirectory.Ensure(config.DataRoot);
        AdminOnlyDirectory.Ensure(config.InstallDirectory);

        // The mode lives out of band in the admin-only data root, never in the approvals files.
        var mode = ModeResolver.Resolve(TbsTpmDetector.Tpm20Present(), ModeFile.Read(config.ModePath), allowNoTpm);
        if (!mode.Ok)
        {
            Console.Error.WriteLine($"refused: {mode.Reason}");
            return 1;
        }

        ModeFile.Write(config.ModePath, mode.Mode);

        IApprovalSigner signer;
        if (mode.Mode == ProtectionMode.Tpm)
        {
            signer = new TpmApprovalSigner(TpmKeys.GetOrCreate(config.KeyName));
        }
        else
        {
            Console.Error.WriteLine("warning: no TPM 2.0; approvals rely on directory ACLs only (no signature).");
            signer = new AclOnlyApprovalSigner();
        }

        var commands = new SetupCommands(
            config,
            elevation,
            new DriveVolumeInfo(),
            new FileSystemPathInspector(),
            new PwshInterpreterInfo(),
            signer,
            new ConsoleConfirmer(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            AdminOnlyDirectory.Ensure);

        // The administrator prompt does not show the folder, and a run can reach this step on its own, so the person sees it here first.
        if (confirmRegister && setupArgs is ["register", var folder] && !new ConsoleConfirmer().Confirm($"Register '{Path.GetFullPath(folder)}' so you can run its approved scripts elevated?"))
        {
            Console.Error.WriteLine("register declined");
            return 1;
        }

        return SetupCli.Run(setupArgs, commands, Console.Out, commands, new ConsoleConfirmer(), commands, commands);
    }
}
