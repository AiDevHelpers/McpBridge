using System.Diagnostics;
using AiDevHelpers.Bridge;

namespace AiDevHelpers.Tray;

/// <summary>One row of the Instances page.</summary>
public sealed record InstanceRow(int Pid, string Ide, string Repo, string Branch, string Path, DateTime Started);

/// <summary>Reads a process's start time by pid. Null when the process has gone.</summary>
public interface IProcessInfo
{
    DateTime? StartTime(int pid);
}

/// <summary>The real process check, from <see cref="Process"/>.</summary>
public sealed class SystemProcessInfo : IProcessInfo
{
    public DateTime? StartTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime();
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Turns validated manifests into rows. Instances whose process has gone are dropped.</summary>
public static class InstanceRows
{
    public static IReadOnlyList<InstanceRow> Build(
        IEnumerable<VsInstance> instances,
        IProcessInfo processes,
        Func<string, string?> rootFor,
        Func<string, string?> branchFor)
    {
        var rows = new List<InstanceRow>();
        foreach (var instance in instances)
        {
            var started = processes.StartTime(instance.Pid);
            if (started is null)
            {
                continue;
            }

            var path = instance.SolutionPath ?? string.Empty;
            var root = instance.SolutionPath is null ? null : rootFor(path);
            var repo = root is null ? "not a repo" : System.IO.Path.GetFileName(root.TrimEnd('\\'));
            var branch = root is null ? string.Empty : branchFor(path) ?? "detached";
            rows.Add(new InstanceRow(instance.Pid, instance.Ide, repo, branch, path, started.Value));
        }

        return rows;
    }
}

