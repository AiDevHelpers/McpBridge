using System.Security.Principal;
using AiDevHelpers.ScriptHelper.Core;

namespace AiDevHelpers.ScriptHelper.Client;

/// <summary>The client's connection to the broker: its config, the request client, and the heartbeat the starter shares.</summary>
internal sealed record BrokerLink(ScriptHelperConfig Config, ScriptClient Client, HeartbeatPing Ping);

internal static class Broker
{
    public static BrokerLink Link()
    {
        var config = ConfigRepository.FromEnvironment().ScriptHelper;
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var queue = new RequestQueue(config.QueueDirectory, config.ResultsDirectory);
        var ping = new HeartbeatPing(
            config.HeartbeatPath,
            () => DateTime.UtcNow,
            exe => BrokerInstall.IsTrusted(exe, config.InstallDirectory, config.DataRoot));
        return new BrokerLink(config, new ScriptClient(queue, ping, sid), ping);
    }

    /// <summary>Starts the broker with one UAC prompt if it is not answering. Later calls find it answering.</summary>
    public static Task<BrokerSessionResult> EnsureAsync(BrokerLink link, CancellationToken ct)
    {
        var brokerExe = Path.Combine(link.Config.InstallDirectory, "scripthelper-broker.exe");
        var session = new BrokerSession(link.Ping, new RunAsBrokerStarter(), brokerExe, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), () => DateTime.UtcNow, Task.Delay);
        return session.EnsureRunningAsync(ct);
    }

    /// <summary>Prints a script's output and returns its exit code; a refused request prints its reason and exits 1.</summary>
    public static int Print(ClientResult result)
    {
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
    }
}
