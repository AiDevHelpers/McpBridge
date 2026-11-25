using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AiDevHelpers.Common.Terminals;

/// <summary>
/// The interpreter chosen for a script. Name is "pwsh", "powershell", "cmd", "bash", "python" or "node". Path is null when it is not
/// installed, and InstallHint then says how to get it; nothing is installed here. TerminalTypeId is the matching terminal type from the
/// terminal scan, or null for interpreters that have none.
/// </summary>
public sealed record ResolvedInterpreter(string Name, string? Path, string? Version, string Reason, string? InstallHint, string? TerminalTypeId)
{
    public bool Installed => Path is not null;

    /// <summary>
    /// The arguments that run the script with this interpreter, without a shell in between. A visible terminal run (<paramref name="interactive"/>)
    /// leaves out PowerShell's -NonInteractive, so the script may prompt the person.
    /// </summary>
    public IReadOnlyList<string> BuildArguments(string scriptPath, IReadOnlyList<string> scriptArguments, bool interactive = false) => Name switch
    {
        "pwsh" or "powershell" => interactive
            ? ["-NoProfile", "-File", scriptPath, .. scriptArguments]
            : ["-NoProfile", "-NonInteractive", "-File", scriptPath, .. scriptArguments],
        "cmd" => ["/c", scriptPath, .. scriptArguments],
        _ => [scriptPath, .. scriptArguments],
    };
}

/// <summary>No interpreter fits the script; Reason says why (an unknown file type).</summary>
public sealed record UnsupportedScript(string Reason);

/// <summary>
/// Picks the interpreter for a script instead of assuming one. The order is: the person's per-extension override, a <c>#!</c> line
/// (matched by interpreter name only, never by a path the file names), PowerShell's <c>#Requires</c> markers, then the file extension.
/// Interpreters are found from PATH and the usual install folders, so a repo cannot choose which program runs.
/// </summary>
public static partial class ScriptInterpreterResolver
{
    private const int HeaderBytes = 4096;

    /// <summary>The names an override may use.</summary>
    public static readonly IReadOnlyList<string> Names = ["pwsh", "powershell", "cmd", "bash", "python", "node"];

    /// <returns>The interpreter, or null with <paramref name="unsupported"/> set when the file type is not one the helper runs.</returns>
    public static ResolvedInterpreter? Resolve(
        ITerminalProbe probe,
        string scriptPath,
        string? repoRoot,
        IReadOnlyDictionary<string, string>? overrides,
        out UnsupportedScript? unsupported)
    {
        unsupported = null;
        var extension = Path.GetExtension(scriptPath).ToLowerInvariant();
        var header = Header(probe, scriptPath);

        if (overrides is not null && overrides.TryGetValue(extension, out var chosen) && Names.Contains(chosen, StringComparer.OrdinalIgnoreCase))
            return For(probe, chosen.ToLowerInvariant(), repoRoot, "the per-user override for " + extension);

        if (ShebangName(header) is { } shebang)
            return For(probe, shebang, repoRoot, "the #! line names " + shebang);

        switch (extension)
        {
            case ".ps1":
                return PowerShell(probe, header);
            case ".cmd" or ".bat":
                return For(probe, "cmd", repoRoot, "a " + extension + " file runs in cmd");
            case ".sh":
                return For(probe, "bash", repoRoot, "a .sh file runs in Git Bash");
            case ".py":
                return For(probe, "python", repoRoot, "a .py file runs in Python");
            case ".js" or ".mjs" or ".cjs":
                return For(probe, "node", repoRoot, "a " + extension + " file runs in Node");
            default:
                unsupported = new UnsupportedScript("no interpreter is known for '" + extension + "' files");
                return null;
        }
    }

    private static ResolvedInterpreter PowerShell(ITerminalProbe probe, string header)
    {
        var requires = header.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("#requires", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var line in requires)
        {
            if (PsEdition().Match(line) is { Success: true } edition)
            {
                var core = edition.Groups["e"].Value.Equals("core", StringComparison.OrdinalIgnoreCase);
                return For(probe, core ? "pwsh" : "powershell", null, "#Requires -PSEdition " + edition.Groups["e"].Value);
            }

            if (PsVersion().Match(line) is { Success: true } version && int.TryParse(version.Groups["v"].Value, out var major))
                return For(probe, major >= 6 ? "pwsh" : "powershell", null, "#Requires -Version " + major);
        }

        var seven = For(probe, "pwsh", null, "no #Requires marker; PowerShell 7 is preferred");
        return seven.Installed ? seven : For(probe, "powershell", null, "no #Requires marker and PowerShell 7 is not installed");
    }

    private static ResolvedInterpreter For(ITerminalProbe probe, string name, string? repoRoot, string reason)
    {
        var (path, hint, terminal) = name switch
        {
            "pwsh" => (ToolLocator.Pwsh(probe), "winget install Microsoft.PowerShell", "pwsh"),
            "powershell" => (ToolLocator.WindowsPowerShell(probe), null, "powershell"),
            "cmd" => (ToolLocator.Cmd(probe), null, "cmd"),
            "bash" => (ToolLocator.GitBash(probe), "winget install Git.Git", "gitbash"),
            "python" => (ToolLocator.Python(probe, repoRoot), "winget install Python.Python.3.13", (string?)null),
            _ => (ToolLocator.Node(probe), "winget install OpenJS.NodeJS.LTS", (string?)null),
        };

        var installed = path is not null && probe.FileExists(path);
        return new ResolvedInterpreter(name, installed ? path : null, installed ? probe.FileVersion(path!) : null, reason, installed ? null : hint ?? "this should be part of Windows", terminal);
    }

    // The interpreter named on a "#!" first line, mapped to a known name; null when there is no #! line or the name is not known.
    private static string? ShebangName(string header)
    {
        var first = header.Split('\n')[0].TrimEnd('\r');
        if (!first.StartsWith("#!", StringComparison.Ordinal))
            return null;

        var words = first[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count == 0)
            return null;

        var program = Path.GetFileName(words[0].Replace('\\', '/'));
        if (program == "env" && words.Count > 1)
            program = Path.GetFileName(words[1].Replace('\\', '/'));

        program = Regex.Replace(program.ToLowerInvariant(), @"[\d.]+$", string.Empty);
        return program switch
        {
            "pwsh" => "pwsh",
            "powershell" => "powershell",
            "bash" or "sh" or "zsh" => "bash",
            "python" or "py" => "python",
            "node" => "node",
            _ => null,
        };
    }

    private static string Header(ITerminalProbe probe, string scriptPath)
    {
        var text = probe.ReadText(scriptPath) ?? string.Empty;
        return text.Length <= HeaderBytes ? text : text[..HeaderBytes];
    }

    [GeneratedRegex(@"-PSEdition\s+(?<e>Core|Desktop)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PsEdition();

    [GeneratedRegex(@"-Version\s+(?<v>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PsVersion();

    /// <summary>The extension to interpreter-name overrides in a repo's <c>user.json</c> (block "interpreters"), or empty.</summary>
    public static IReadOnlyDictionary<string, string> ReadOverrides(string? repoRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = repoRoot is null ? null : RepoSettingsFile.UserFilePath(repoRoot);
            if (path is not null && File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["interpreters"] is JsonObject block)
            {
                foreach (var (key, value) in block)
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var name) && Names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        result[key.StartsWith('.') ? key : "." + key] = name;
                }
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            // An unreadable file means no overrides.
        }

        return result;
    }
}
