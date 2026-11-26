using System;
using System.IO;
using System.Text.Json;

namespace AiDevHelpers.Bridge;

/// <summary>
/// Writes one manifest per running Visual Studio instance to %ProgramData%\AiDevHelpers\vs-instances\&lt;pid&gt;.json
/// so MCP can discover instances and the solution each one has open. The shape is <see cref="VsInstance"/>,
/// read by <see cref="VsInstanceRegistry"/>.
/// </summary>
public static class InstanceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The folder that holds one manifest per running instance: %ProgramData%\AiDevHelpers\vs-instances.</summary>
    public static string Directory =>
        Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "AiDevHelpers", "vs-instances");

    /// <summary>The request pipe name for a Visual Studio process id.</summary>
    public static string PipeNameFor(int pid) => $"AiDevHelpers.VS.{pid}";

    /// <summary>Events pipe for the instance: pushes events to subscribed clients (see conformance/protocol.md).</summary>
    public static string EventPipeNameFor(int pid) => $"AiDevHelpers.VS.{pid}.events";

    /// <summary>Writes this instance's manifest, atomically, with its open solution path.</summary>
    public static void Write(int pid, string? solutionPath)
    {
        var record = new VsInstance(pid, PipeNameFor(pid), solutionPath, DateTime.UtcNow, "VisualStudio", Array.Empty<string>());
        System.IO.Directory.CreateDirectory(Directory);

        var path = ManifestPath(pid);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(record, JsonOptions));
#if NETFRAMEWORK
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        File.Move(tempPath, path);
#else
        File.Move(tempPath, path, overwrite: true);
#endif
    }

    /// <summary>Deletes the manifest for the process id. A missing or locked file is ignored.</summary>
    public static void Remove(int pid)
    {
        try
        {
            File.Delete(ManifestPath(pid));
        }
        catch (IOException)
        {
            // Already gone or locked by a reader; the MCP side drops manifests whose pid is no longer running.
        }
    }

    private static string ManifestPath(int pid) => Path.Combine(Directory, $"{pid}.json");
}
