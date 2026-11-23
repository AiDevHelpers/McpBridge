using System.Security.Principal;
using AiDevHelpers.ScriptHelper.Core;

// elev <projectRoot> <script> [args...]
// Runs an approved script through the broker, unelevated. Prints the script's output and exits
// with its exit code. Exit 1 means the request was refused or could not be answered.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: elev <projectRoot> <script> [args...]");
    return 2;
}

var config = ConfigRepository.FromEnvironment().ScriptHelper;
var sid = WindowsIdentity.GetCurrent().User!.Value;
var queue = new RequestQueue(config.QueueDirectory, config.ResultsDirectory);
var ping = new HeartbeatPing(
    config.HeartbeatPath,
    () => DateTime.UtcNow,
    exe => BrokerInstall.IsTrusted(exe, config.InstallDirectory, config.DataRoot));
var client = new ScriptClient(queue, ping, sid);

// First elevated request of a session: start the broker with one UAC prompt. Later requests find it answering.
var brokerExe = Path.Combine(config.InstallDirectory, "scripthelper-broker.exe");
var session = new BrokerSession(ping, new RunAsBrokerStarter(), brokerExe, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), () => DateTime.UtcNow, Task.Delay);
var ensured = session.EnsureRunningAsync(CancellationToken.None).GetAwaiter().GetResult();
if (!ensured.Ok)
{
    Console.Error.WriteLine($"refused: {ensured.Reason}");
    return 1;
}

var result = client.Run(args[0], args[1], args[2..], TimeSpan.FromMinutes(10));

if (!string.IsNullOrEmpty(result.Stdout))
{
    Console.Out.Write(result.Stdout.EndsWith('\n') ? result.Stdout : result.Stdout + "\n");
}

if (!string.IsNullOrEmpty(result.Stderr))
{
    Console.Error.Write(result.Stderr.EndsWith('\n') ? result.Stderr : result.Stderr + "\n");
}

if (!result.Ok)
{
    Console.Error.WriteLine($"refused: {result.Reason}");
    return 1;
}

return result.ExitCode ?? 1;
