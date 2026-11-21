using AiDevHelpers.ScriptHelper.Core;

// Elevated setup for Script Helper: register a project root, or approve a script for it.
// Run from an elevated shell. All logic lives in the Core library; this file only wires the real parts.

var allowNoTpm = args.Contains("--allow-no-tpm", StringComparer.Ordinal);
var commandArgs = args.Where(a => a != "--allow-no-tpm").ToArray();

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

return SetupCli.Run(commandArgs, commands, Console.Out);
