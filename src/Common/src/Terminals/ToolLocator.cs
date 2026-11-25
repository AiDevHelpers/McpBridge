namespace AiDevHelpers.Common.Terminals;

/// <summary>
/// Where the interpreters and shells live on a machine. The paths come from PATH and from the usual install folders, never from
/// anything a repo says. A path is returned even when the file is missing only when it is the expected install location, so callers
/// check <see cref="ITerminalProbe.FileExists"/>.
/// </summary>
public static class ToolLocator
{
    public static string? Pwsh(ITerminalProbe probe)
    {
        var onPath = probe.FindOnPath("pwsh.exe");
        if (onPath is not null)
            return onPath;

        var programFiles = probe.Folder(Environment.SpecialFolder.ProgramFiles);
        return programFiles is null ? null : Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
    }

    public static string? WindowsPowerShell(ITerminalProbe probe)
    {
        var system = probe.Folder(Environment.SpecialFolder.System);
        return system is null ? null : Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    public static string? Cmd(ITerminalProbe probe)
    {
        var system = probe.Folder(Environment.SpecialFolder.System);
        return system is null ? null : Path.Combine(system, "cmd.exe");
    }

    /// <summary>Git for Windows' bash. Never the WSL launcher in System32, which is also called bash.exe.</summary>
    public static string? GitBash(ITerminalProbe probe)
    {
        var git = probe.FindOnPath("git.exe");
        var programFiles = probe.Folder(Environment.SpecialFolder.ProgramFiles);
        var localApps = probe.Folder(Environment.SpecialFolder.LocalApplicationData);
        string?[] candidates =
        [
            git is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(git)!, "..", "bin", "bash.exe")),
            programFiles is null ? null : Path.Combine(programFiles, "Git", "bin", "bash.exe"),
            localApps is null ? null : Path.Combine(localApps, "Programs", "Git", "bin", "bash.exe"),
        ];

        return candidates.FirstOrDefault(c => c is not null && probe.FileExists(c)) ?? candidates.FirstOrDefault(c => c is not null);
    }

    public static string? Node(ITerminalProbe probe) => probe.FindOnPath("node.exe");

    /// <summary>The repo's own virtual environment first, then the py launcher, then python on PATH. The Store alias stub is skipped.</summary>
    public static string? Python(ITerminalProbe probe, string? repoRoot)
    {
        if (repoRoot is not null)
        {
            var venv = Path.Combine(repoRoot, ".venv", "Scripts", "python.exe");
            if (probe.FileExists(venv))
                return venv;
        }

        var launcher = probe.FindOnPath("py.exe");
        if (launcher is not null)
            return launcher;

        var python = probe.FindOnPath("python.exe");
        return python is not null && !python.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) ? python : null;
    }
}
