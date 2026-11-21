using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace AiDevHelpers.ScriptHelper.Core;

// The interfaces these classes implement (IElevationCheck, IVolumeInfo, IPathInspector,
// IInterpreterInfo, IConfirmer) live in AiDevHelpers.Common.Contracts.

[SupportedOSPlatform("windows")]
public sealed class WindowsElevationCheck : IElevationCheck
{
    public bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

public sealed class DriveVolumeInfo : IVolumeInfo
{
    public string? FileSystem(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? null : new DriveInfo(root).DriveFormat;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

public sealed class FileSystemPathInspector : IPathInspector
{
    public bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
}

/// <summary>Describes pwsh by its resolved path and file version, found on PATH.</summary>
public sealed class PwshInterpreterInfo : IInterpreterInfo
{
    public string Describe()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, "pwsh.exe");
            if (File.Exists(candidate))
            {
                var version = FileVersionInfo.GetVersionInfo(candidate).ProductVersion ?? "unknown";
                return $"pwsh|{Path.GetFullPath(candidate)}|{version}";
            }
        }

        return "pwsh|missing";
    }
}

/// <summary>Prompts on the console and accepts only an explicit "y". Redirected input is never a yes.</summary>
public sealed class ConsoleConfirmer : IConfirmer
{
    public bool Confirm(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            return false;
        }

        Console.WriteLine(prompt);
        Console.Write("Approve? [y/N] ");
        var answer = Console.ReadLine();
        return string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
    }
}
