using System.Diagnostics;
using System.Management;

namespace AiDevHelpers.Tray;

/// <summary>
/// The process facts the instances page shows, read from the live process by pid. A process that has gone gives null, and its row is
/// dropped. These are the production readings; the row builder takes them as functions so the tests do not need a process.
/// </summary>
internal static class InstanceProcessFacts
{
    /// <summary>The process start time in UTC, or null when the process has gone.</summary>
    public static DateTime? StartedAt(int pid)
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

    /// <summary>The account that runs the process, as DOMAIN\user, or null when it cannot be read (a process of another user, for example).</summary>
    public static string? OwnerOf(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (var process in searcher.Get().Cast<ManagementObject>())
            {
                var owner = new object[] { null!, null! };
                var result = process.InvokeMethod("GetOwner", owner);
                if (Convert.ToUInt32(result) == 0)
                {
                    return $"{owner[1]}\\{owner[0]}";
                }
            }
        }
        catch (ManagementException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}
