using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AiDevHelpers.Bridge;

/// <summary>
/// Records a Visual Studio launch that has started but whose bridge has not written its manifest yet, so
/// ide_list_instances can show it as starting. A marker is removed once its pid has a manifest or has exited.
/// </summary>
public static class StartingInstances
{
    private static string Folder => VsInstanceRegistry.Directory;

    private static string FileFor(int pid) => Path.Combine(Folder, $"starting-{pid}.json");

    public static void Write(int pid, string solutionPath)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(
                FileFor(pid),
                JsonSerializer.Serialize(new { pid, solution_path = solutionPath, started_utc = DateTime.UtcNow }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Listing is informational; a launch must not fail because the marker could not be written.
        }
    }

    public static void Clear(int pid)
    {
        try
        {
            File.Delete(FileFor(pid));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed on a later listing.
        }
    }

    /// <summary>Markers whose process is still running and whose bridge has not registered yet. Others are removed.</summary>
    public static IReadOnlyList<object> List()
    {
        var result = new List<object>();
        if (!Directory.Exists(Folder))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(Folder, "starting-*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var pid = doc.RootElement.GetProperty("pid").GetInt32();
                var solution = doc.RootElement.GetProperty("solution_path").GetString();
                var started = doc.RootElement.GetProperty("started_utc").GetDateTime();
                var registered = VsInstanceRegistry.ListRunning().Any(i => i.Pid == pid);
                if (registered || !IsRunning(pid))
                {
                    Clear(pid);
                    continue;
                }

                result.Add(new { pid, solution_path = solution, started_utc = started, bridge_ready = false });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // Unreadable marker: skip it.
            }
        }

        return result;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
