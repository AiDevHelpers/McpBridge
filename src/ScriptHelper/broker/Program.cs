using AiDevHelpers.ScriptHelper.Core;

// The elevated broker. Start it once with one UAC prompt, hidden:
//   Start-Process -FilePath scripthelper-broker.exe -Verb RunAs -WindowStyle Hidden
// It takes a single-instance lock, answers requests from the queue, and stops on the stop file
// or after the idle timeout. Every decision and the stop reason go to the admin-only audit log.

const string LockName = @"Local\AiDevHelpers.ScriptHelper.Broker";
var idleTimeout = TimeSpan.FromMinutes(30);

var elevation = new WindowsElevationCheck();
if (!elevation.IsElevated())
{
    Console.Error.WriteLine("refused: the broker must be started elevated");
    return 1;
}

var config = ConfigRepository.FromEnvironment().ScriptHelper;

var mode = ModeFile.Read(config.ModePath);
if (mode is null)
{
    Console.Error.WriteLine("refused: no protection mode recorded; run scripthelper-setup first");
    return 1;
}

using var brokerLock = BrokerLock.TryAcquire(LockName);
if (brokerLock is null)
{
    Console.Error.WriteLine("refused: another broker is already running");
    return 1;
}

IApprovalSigner signer = mode.Value == ProtectionMode.Tpm
    ? new TpmApprovalSigner(TpmKeys.GetOrCreate(config.KeyName))
    : new AclOnlyApprovalSigner();

Directory.CreateDirectory(config.StagingDirectory);

void Audit(string line) => AuditLog.Append(config.AuditLogPath, DateTime.UtcNow, line);

var queue = new RequestQueue(config.QueueDirectory, config.ResultsDirectory);
var registry = new ProjectRegistry(
    config.RegistryPath,
    elevation,
    new DriveVolumeInfo(),
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
var handler = new RequestHandler(
    registry,
    signer,
    new VersionCounter(config.VersionsDirectory),
    config,
    new FileSystemPathInspector(),
    new PwshInterpreterInfo(),
    new ProcessScriptRunner(),
    config.StagingDirectory,
    Audit);
var loop = new BrokerLoop(queue, handler.Handle);

Audit($"broker started (pid {Environment.ProcessId}, mode {mode.Value})");
var host = new BrokerHost(
    loop,
    config.HeartbeatPath,
    config.StopPath,
    idleTimeout,
    () => DateTime.UtcNow,
    Audit,
    Environment.ProcessId,
    mode.Value);

host.Run();
return 0;
