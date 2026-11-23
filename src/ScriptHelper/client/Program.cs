using System.Security.Principal;
using AiDevHelpers.ScriptHelper.Core;

// elev <projectRoot> <script> [args...]   |   elev --verify <projectRoot>   |   elev --repo-scan <root>   |   elev repo-setup <root>
// Runs an approved script through the broker, unelevated. Prints the script's output and exits
// with its exit code. Exit 1 means the request was refused or could not be answered.

// Local verbs need no broker and no ScriptHelper configuration, so they run before either is touched.
if (ElevLocalCommands.TryRun(args, Console.Out, new ConsoleConfirmer(), out var localExit))
{
    return localExit;
}

// elev --interactive <projectRoot> <script> [args...]: the same approved script, in a visible window; the transcript is printed.
var visibleRun = args.Length > 0 && args[0] == "--interactive";
if (visibleRun)
{
    args = args[1..];
}

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: elev <projectRoot> <script> [args...]");
    return 2;
}

var config = ConfigRepository.FromEnvironment().ScriptHelper;
// elev --install-tool <name>: the installer script the admin approved with "setup tools", run through the broker. The name is checked
// against the same table the script uses; the project folder is the admin-only tools folder, never a path from the caller.
if (args is ["--install-tool", var toolName])
{
    if (AiDevHelpers.Common.InstallableTools.TryGet(toolName) is not { } tool)
    {
        Console.Error.WriteLine($"refused: '{toolName}' is not a tool this helper installs");
        return 2;
    }

    args = [InstallToolFiles.ToolsRoot(config), InstallToolFiles.ScriptName, tool.Name];
}

var sid = WindowsIdentity.GetCurrent().User!.Value;
var queue = new RequestQueue(config.QueueDirectory, config.ResultsDirectory);
var ping = new HeartbeatPing(
    config.HeartbeatPath,
    () => DateTime.UtcNow,
    exe => BrokerInstall.IsTrusted(exe, config.InstallDirectory, config.DataRoot));
var client = new ScriptClient(queue, ping, sid);

// elev --verify <projectRoot>: read-only. Prints the approval status of every allowlisted file as JSON. Exit 0 all approved,
// 1 some are not, 2 the broker did not answer (it is never started for this).
if (args.Length == 2 && args[0] == "--verify")
{
    var verified = await client.VerifyAsync(args[1], TimeSpan.FromSeconds(15));
    if (!verified.Ok)
    {
        Console.Error.WriteLine($"unverified: {verified.Reason}");
        return 2;
    }

    Console.Out.WriteLine(verified.Stdout);
    return verified.ExitCode ?? 2;
}

// First elevated request of a session: start the broker with one UAC prompt. Later requests find it answering.
var brokerExe = Path.Combine(config.InstallDirectory, "scripthelper-broker.exe");
var session = new BrokerSession(ping, new RunAsBrokerStarter(), brokerExe, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), () => DateTime.UtcNow, Task.Delay);
var ensured = session.EnsureRunningAsync(CancellationToken.None).GetAwaiter().GetResult();
if (!ensured.Ok)
{
    Console.Error.WriteLine($"refused: {ensured.Reason}");
    return 1;
}

var result = visibleRun
    ? await client.InteractiveAsync(args[0], args[1], args[2..], TimeSpan.FromMinutes(10))
    : await client.RunAsync(args[0], args[1], args[2..], TimeSpan.FromMinutes(10));

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
