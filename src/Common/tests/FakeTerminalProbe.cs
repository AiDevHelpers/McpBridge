using System.Text;
using AiDevHelpers.Common.Terminals;

namespace AiDevHelpers.Common.Tests;

/// <summary>A machine for the terminal and interpreter tests: which files exist, what is on PATH, and what files say.</summary>
internal sealed class FakeTerminalProbe : ITerminalProbe
{
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> OnPath { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Texts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? WslOutput { get; set; }

    public string? VsInstall { get; set; }

    public List<string> Settings { get; } = [];

    /// <summary>A Windows machine with Windows PowerShell, cmd, WSL and Git for Windows, and nothing else.</summary>
    public static FakeTerminalProbe Machine()
    {
        var probe = new FakeTerminalProbe();
        probe.Files.UnionWith([
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            @"C:\Windows\System32\cmd.exe",
            @"C:\Windows\System32\wsl.exe",
            @"C:\Program Files\Git\bin\bash.exe",
        ]);
        probe.OnPath["git.exe"] = @"C:\Program Files\Git\cmd\git.exe";
        probe.WslOutput = "Ubuntu\0\r\0\n\0docker-desktop\0\r\0\n\0";
        return probe;
    }

    public string? FindOnPath(string fileName) => OnPath.GetValueOrDefault(fileName);

    public bool FileExists(string path) => Files.Contains(path);

    public string? FileVersion(string path) => "1.2.3";

    public string? Folder(Environment.SpecialFolder folder) => folder switch
    {
        Environment.SpecialFolder.System => @"C:\Windows\System32",
        Environment.SpecialFolder.ProgramFiles => @"C:\Program Files",
        Environment.SpecialFolder.ProgramFilesX86 => @"C:\Program Files (x86)",
        Environment.SpecialFolder.LocalApplicationData => @"C:\Users\u\AppData\Local",
        _ => null,
    };

    public string? Run(string fileName, IReadOnlyList<string> arguments, Encoding encoding) =>
        fileName.EndsWith("wsl.exe", StringComparison.OrdinalIgnoreCase) ? WslOutput : VsInstall;

    public IReadOnlyList<string> WindowsTerminalSettingsFiles() => Settings.Select((_, i) => "settings" + i).ToList();

    public string? ReadText(string path) =>
        path.StartsWith("settings", StringComparison.Ordinal) && int.TryParse(path[8..], out var i) && i < Settings.Count
            ? Settings[i]
            : Texts.GetValueOrDefault(path);
}
