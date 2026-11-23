using System.Diagnostics;
using System.Text;

namespace AiDevHelpers.Common.Terminals;

/// <summary>
/// One kind of terminal a script can run in. Id is stable ("pwsh", "powershell", "cmd", "gitbash", "vsdevshell", "wsl:Ubuntu").
/// CommandLine is what a Windows Terminal profile for it runs.
/// </summary>
public sealed record TerminalType(string Id, string Name, string? Path, string? Version, string CommandLine, bool Installed, bool InHelper, bool InWindowsTerminal, bool Declined)
{
    /// <summary>Installed but not registered for the helper, and not declined: setup asks about it.</summary>
    public bool Missing => Installed && !InHelper && !Declined;
}

/// <summary>What a scan found. Hints say how to get a type that is not installed at all; the scan installs nothing.</summary>
public sealed record TerminalScan(IReadOnlyList<TerminalType> Types, IReadOnlyList<string> Hints, bool WindowsTerminalFound);

/// <summary>The machine facts the scanner needs. The seam keeps the scanner testable without the real machine.</summary>
public interface ITerminalProbe
{
    /// <summary>The full path of an executable found on PATH, or null.</summary>
    string? FindOnPath(string fileName);

    bool FileExists(string path);

    /// <summary>The file's version text, or null.</summary>
    string? FileVersion(string path);

    /// <summary>A known folder, such as the Windows folder or Program Files; null when it is not set.</summary>
    string? Folder(Environment.SpecialFolder folder);

    /// <summary>Runs a program and returns what it printed, decoded with the given encoding, or null when it could not run.</summary>
    string? Run(string fileName, IReadOnlyList<string> arguments, Encoding encoding);

    /// <summary>The Windows Terminal settings files that exist (packaged, Preview, unpackaged), as full paths.</summary>
    IReadOnlyList<string> WindowsTerminalSettingsFiles();

    string? ReadText(string path);
}

/// <summary>Probes the real machine.</summary>
public sealed class SystemTerminalProbe : ITerminalProbe
{
    public string? FindOnPath(string fileName)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A PATH entry with illegal characters is skipped.
            }
        }

        return null;
    }

    public bool FileExists(string path) => File.Exists(path);

    public string? FileVersion(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).ProductVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return null;
        }
    }

    public string? Folder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        return path.Length == 0 ? null : path;
    }

    public string? Run(string fileName, IReadOnlyList<string> arguments, Encoding encoding)
    {
        try
        {
            var info = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = encoding,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(15000) && process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> WindowsTerminalSettingsFiles()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(local, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json"),
            Path.Combine(local, "Packages", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe", "LocalState", "settings.json"),
            Path.Combine(local, "Microsoft", "Windows Terminal", "settings.json"),
        ];
        return [.. candidates.Where(File.Exists)];
    }

    public string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Finds the terminal types on this machine and says which ones the helper and Windows Terminal already know. Read only: it
/// installs nothing and writes nothing. A type that is not installed at all is only a hint.
/// </summary>
public sealed class TerminalTypeScanner(ITerminalProbe probe, TerminalRegistry registry)
{
    public TerminalScan Scan()
    {
        var registered = registry.Read();
        var settings = probe.WindowsTerminalSettingsFiles().Select(probe.ReadText).OfType<string>().ToList();
        var hints = new List<string>();
        var types = new List<TerminalType>();

        void Add(string id, string name, string? path, string commandLine, string generator)
        {
            var installed = path is not null && probe.FileExists(path);
            types.Add(new TerminalType(
                id,
                name,
                installed ? path : null,
                installed ? probe.FileVersion(path!) : null,
                commandLine,
                installed,
                registered.Registered.Any(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)),
                installed && settings.Any(s => WindowsTerminalSettings.Covers(s, id, name, path, generator)),
                registered.Declined.Contains(id, StringComparer.OrdinalIgnoreCase)));
        }

        var system = probe.Folder(Environment.SpecialFolder.System);
        var programFiles = probe.Folder(Environment.SpecialFolder.ProgramFiles);
        var localApps = probe.Folder(Environment.SpecialFolder.LocalApplicationData);

        var pwsh = probe.FindOnPath("pwsh.exe") ?? (programFiles is null ? null : Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));
        Add("pwsh", "PowerShell 7", pwsh, Quote(pwsh) + " -NoLogo", "Windows.Terminal.PowershellCore");
        if (pwsh is null || !probe.FileExists(pwsh))
            hints.Add("PowerShell 7 is not installed: winget install Microsoft.PowerShell");

        Add("powershell", "Windows PowerShell 5.1", system is null ? null : Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"), "powershell.exe -NoLogo", string.Empty);
        Add("cmd", "Command Prompt", system is null ? null : Path.Combine(system, "cmd.exe"), "cmd.exe", string.Empty);

        var bash = GitBash(programFiles, localApps);
        Add("gitbash", "Git Bash", bash, Quote(bash) + " -i -l", "Git");
        if (bash is null || !probe.FileExists(bash))
            hints.Add("Git Bash is not installed: winget install Git.Git");

        var wsl = system is null ? null : Path.Combine(system, "wsl.exe");
        var distros = wsl is not null && probe.FileExists(wsl) ? WslDistros(wsl) : [];
        foreach (var distro in distros)
            Add("wsl:" + distro, "WSL " + distro, wsl, "wsl.exe -d " + distro, "Windows.Terminal.Wsl");

        if (distros.Count == 0)
            hints.Add("No WSL distribution is installed: wsl --install (needs a restart)");

        var devShell = DevShell();
        Add("vsdevshell", "Visual Studio Developer PowerShell", devShell, "powershell.exe -NoExit -Command \"& '" + devShell + "'\"", "Windows.Terminal.VisualStudio");

        return new TerminalScan(types, hints, settings.Count > 0);
    }

    private string? GitBash(string? programFiles, string? localApps)
    {
        var git = probe.FindOnPath("git.exe");
        var fromGit = git is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(git)!, "..", "bin", "bash.exe"));
        string?[] candidates =
        [
            fromGit,
            programFiles is null ? null : Path.Combine(programFiles, "Git", "bin", "bash.exe"),
            localApps is null ? null : Path.Combine(localApps, "Programs", "Git", "bin", "bash.exe"),
        ];

        // Never the WSL launcher in System32, which is also called bash.exe.
        return candidates.FirstOrDefault(c => c is not null && probe.FileExists(c)) ?? candidates.FirstOrDefault(c => c is not null);
    }

    private List<string> WslDistros(string wsl)
    {
        var output = probe.Run(wsl, ["-l", "-q"], Encoding.Unicode);
        if (output is null)
            return [];

        return [.. output.Replace("\0", string.Empty).Replace("﻿", string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))];
    }

    private string? DevShell()
    {
        var installer = probe.Folder(Environment.SpecialFolder.ProgramFilesX86);
        var vswhere = installer is null ? null : Path.Combine(installer, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (vswhere is null || !probe.FileExists(vswhere))
            return null;

        var install = probe.Run(vswhere, ["-latest", "-products", "*", "-property", "installationPath"], Encoding.UTF8)?.Trim();
        return string.IsNullOrEmpty(install) ? null : Path.Combine(install, "Common7", "Tools", "Launch-VsDevShell.ps1");
    }

    private static string Quote(string? path) => "\"" + path + "\"";
}
