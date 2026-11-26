using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiDevHelpers.Helpers.RoslynHelper;

/// <summary>One helper process's entry in the manifest folder: <c>{"pid","pipeName","solutionPath"}</c>.</summary>
public sealed record Manifest(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("pipeName")] string PipeName,
    [property: JsonPropertyName("solutionPath")] string SolutionPath);

/// <summary>Writes, reads and removes <c>&lt;pid&gt;.json</c> manifests in the helper folder.</summary>
public static class ManifestWriter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Writes the manifest atomically: a temp file in the same folder, then a move over the target.</summary>
    public static void Write(string folder, int pid, string pipeName, string solutionPath)
    {
        Directory.CreateDirectory(folder);
        var target = PathFor(folder, pid);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(new Manifest(pid, pipeName, solutionPath), Options);
        File.WriteAllText(temp, json);
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>Returns the manifest, or null when it is missing, unreadable, or its process is not running.</summary>
    public static Manifest? Read(string folder, int pid)
    {
        var target = PathFor(folder, pid);
        if (!File.Exists(target) || !IsRunning(pid))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(target));
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Deletes the manifest. A missing file is not an error.</summary>
    public static void Remove(string folder, int pid)
    {
        var target = PathFor(folder, pid);
        if (File.Exists(target))
        {
            File.Delete(target);
        }
    }

    private static string PathFor(string folder, int pid) => Path.Combine(folder, $"{pid}.json");

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
