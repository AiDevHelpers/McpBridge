using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>
/// Lists test host processes (testhost.exe) that a finished or hung run left behind, and stops them. Two steps, like git_plan:
/// the list returns a token, and the stop runs only with that token, once, after the user agreed in chat.
/// </summary>
[McpServerToolType]
internal static class TestHostTools
{
    private static readonly string[] HostNames = ["testhost", "testhost.x86"];
    private static readonly ConcurrentDictionary<string, IReadOnlyList<HostRef>> Plans = new();

    private sealed record HostRef(int Pid, DateTime StartUtc, string Name);

    [McpServerTool]
    [Description("List test host processes (testhost.exe) that are still running, such as ones a run left holding the test outputs. " +
                 "Read only. Returns a token for test_hosts_stop, which kills only these processes.")]
    public static string TestHostsList()
    {
        var hosts = Find();
        if (hosts.Count == 0)
        {
            return JsonSerializer.Serialize(new { ok = true, count = 0 });
        }

        var token = Guid.NewGuid().ToString("N");
        Plans[token] = hosts;
        return JsonSerializer.Serialize(new
        {
            ok = true,
            count = hosts.Count,
            token,
            hosts = hosts.Select(h => new { pid = h.Pid, name = h.Name, started_utc = h.StartUtc }),
        });
    }

    [McpServerTool]
    [Description("Stop the test hosts listed by test_hosts_list, once, with the token it returned. Call it only after the user agreed in chat. " +
                 "Each pid is killed only if it still has the start time the list recorded.")]
    public static string TestHostsStop(
        [Description("Token returned by test_hosts_list")] string token)
    {
        if (!Plans.TryRemove(token, out var hosts))
        {
            return JsonSerializer.Serialize(new { ok = false, error = Strings.Get("TestHostTokenUnknown") });
        }

        var stopped = new List<int>();
        var skipped = new List<object>();
        foreach (var host in hosts)
        {
            try
            {
                using var process = Process.GetProcessById(host.Pid);
                if (process.HasExited || process.StartTime.ToUniversalTime() != host.StartUtc)
                {
                    skipped.Add(new { pid = host.Pid, reason = Strings.Get("TestHostGone") });
                    continue;
                }

                process.Kill(entireProcessTree: true);
                stopped.Add(host.Pid);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                skipped.Add(new { pid = host.Pid, reason = Strings.Get("TestHostGone") });
            }
        }

        return JsonSerializer.Serialize(new { ok = skipped.Count == 0, stopped, skipped });
    }

    private static IReadOnlyList<HostRef> Find()
    {
        var found = new List<HostRef>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!HostNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    found.Add(new HostRef(process.Id, process.StartTime.ToUniversalTime(), process.ProcessName));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Exited while listing, or no access to its start time.
                }
            }
        }

        return found;
    }
}
