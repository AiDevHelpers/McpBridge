using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AiDevHelpers.Common.Processes;

namespace AiDevHelpers.Helpers.WslHelper;

/// <summary>
///     WSL status, health, run and debug setup over <c>wsl.exe</c>. Refusals come back as <c>Ok = false</c> with
///     <c>Error</c> text; ordinary failures (non-zero exit, failed probe, missing wsl.exe) are data, never thrown.
/// </summary>
public sealed class WslService
{
    public const int OutputCapChars = 20_000;
    public const int DefaultRunTimeoutSeconds = 120;
    public const int MaxRunTimeoutSeconds = 600;

    private const string WslExe = "wsl.exe";
    private const string DefaultLabel = "(default)";
    private const string BashPath = "/bin/bash";

    private const string HealthProbe =
        "printf 'KERNEL=%s\\n' \"$(uname -sr)\"; " +
        "printf 'USER=%s\\n' \"$(id -un)\"; " +
        "( . /etc/os-release 2>/dev/null; printf 'OS=%s\\n' \"$PRETTY_NAME\" ); " +
        "if command -v apt-get >/dev/null 2>&1; then printf 'APT=1\\n'; else printf 'APT=0\\n'; fi; " +
        "if sudo -n true >/dev/null 2>&1; then printf 'SUDO=1\\n'; else printf 'SUDO=0\\n'; fi";

    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan BackendProbeTimeout = TimeSpan.FromSeconds(30);

    // \z, not $: $ also matches before a trailing newline, which would let "Ubuntu\n" through.
    private static readonly Regex DistroNamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant);

    private static readonly Regex ListRow = new(@"^\s*(\*)?\s*(\S+)\s+(\S+)\s+(\d+)\s*$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly IWslCommands _commands;

    public WslService()
        : this(new CommandRunnerWslCommands())
    {
    }

    public WslService(IWslCommands commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands = commands;
    }

    /// <summary>True when the name is a safe distro name (no leading dash, no shell or option metacharacters).</summary>
    public static bool IsValidDistroName(string? name) =>
        name is not null && DistroNamePattern.IsMatch(name);

    // ── status ──────────────────────────────────────────────────────────────────

    public async Task<WslStatusResult> StatusAsync(CancellationToken ct = default)
    {
        CommandResult list = await ListAsync(ct);
        if (!list.Launched)
        {
            return new WslStatusResult(true, null, false, 0, [], null);
        }

        if (!list.Success)
        {
            return new WslStatusResult(false, Strings.Format("ListFailed", list.ExitCode, Detail(list)), true, 0, [], null);
        }

        IReadOnlyList<WslDistro> distros = ParseDistroList(list.StdOut);
        return new WslStatusResult(true, null, true, distros.Count, distros, distros.FirstOrDefault(d => d.IsDefault)?.Name);
    }

    // ── health ──────────────────────────────────────────────────────────────────

    public async Task<WslHealthResult> HealthAsync(string? distro = null, CancellationToken ct = default)
    {
        (string? name, string? error) = await ResolveDistroAsync(distro, ct);
        if (name is null)
        {
            return new WslHealthResult(false, error, distro ?? DefaultLabel, false, null, null, null, false, false, null);
        }

        CommandResult r = await RunBashAsync(name, HealthProbe, HealthTimeout, ct);
        if (!r.Success)
        {
            return new WslHealthResult(true, null, name, false, null, null, null, false, false, Detail(r));
        }

        // KEY=VALUE lines, so order does not matter. Text after the first '=' is the value (PRETTY_NAME may contain '=').
        Dictionary<string, string> fields = ParseKeyValues(r.StdOut);
        string? kernel = fields.GetValueOrDefault("KERNEL");
        bool healthy = !string.IsNullOrWhiteSpace(kernel);

        return new WslHealthResult(
            true,
            null,
            name,
            healthy,
            fields.GetValueOrDefault("OS"),
            kernel,
            fields.GetValueOrDefault("USER"),
            fields.GetValueOrDefault("APT") == "1",
            fields.GetValueOrDefault("SUDO") == "1",
            healthy ? null : Strings.Get("HealthNoKernel"));
    }

    // ── run ─────────────────────────────────────────────────────────────────────

    /// <summary>Runs <c>command</c> through <c>/bin/bash -c</c> in the distro. Runs arbitrary commands in the distro.</summary>
    public async Task<WslRunResult> RunAsync(
        string command,
        string? distro = null,
        int timeoutSeconds = DefaultRunTimeoutSeconds,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return RunRefused(Strings.Get("CommandRequired"), distro);
        }

        if (timeoutSeconds < 1 || timeoutSeconds > MaxRunTimeoutSeconds)
        {
            return RunRefused(Strings.Get("TimeoutInvalid"), distro);
        }

        (string? name, string? error) = await ResolveDistroAsync(distro, ct);
        if (name is null)
        {
            return RunRefused(error!, distro);
        }

        CommandResult r = await RunBashAsync(name, command, TimeSpan.FromSeconds(timeoutSeconds), ct);
        if (!r.Launched)
        {
            return new WslRunResult(false, null, name, -1, "", "", false, false, Strings.Format("LaunchFailed", r.LaunchError));
        }

        (string stdout, bool stdoutCapped) = CapAndTrim(r.StdOut, r.StdOutTruncated);
        (string stderr, bool stderrCapped) = CapAndTrim(r.StdErr, r.StdErrTruncated);
        bool capped = stdoutCapped || stderrCapped;

        if (r.TimedOut)
        {
            return new WslRunResult(false, Strings.Format("RunTimedOut", timeoutSeconds), name, -1, stdout, stderr, true, capped, null);
        }

        return new WslRunResult(true, null, name, r.ExitCode, stdout, stderr, false, capped, null);
    }

    // ── debug_setup ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Checks the runtime's debugger backend in the distro and builds a VS Code launch object. With
    ///     <paramref name="write" /> it creates <c>.vscode/launch.json</c> under <paramref name="workspaceRoot" /> only
    ///     when that file does not exist yet. <paramref name="programPath" /> goes into the JSON only, never a command line.
    /// </summary>
    public async Task<WslDebugSetupResult> DebugSetupAsync(
        string runtime,
        string programPath,
        string? distro = null,
        string? workspaceRoot = null,
        bool write = false,
        CancellationToken ct = default)
    {
        string? canonical = NormalizeRuntime(runtime);
        if (canonical is null)
        {
            return DebugRefused(Strings.Format("RuntimeUnknown", runtime?.Trim() ?? ""), distro, runtime);
        }

        if (string.IsNullOrWhiteSpace(programPath))
        {
            return DebugRefused(Strings.Get("ProgramPathRequired"), distro, canonical);
        }

        string? vscodeDir = null;
        if (write)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                return DebugRefused(Strings.Get("WriteNeedsWorkspace"), distro, canonical);
            }

            if (!Directory.Exists(workspaceRoot))
            {
                return DebugRefused(Strings.Format("WorkspaceNotFolder", workspaceRoot), distro, canonical);
            }

            vscodeDir = Path.Combine(workspaceRoot, ".vscode");
            if (File.Exists(vscodeDir))
            {
                return DebugRefused(Strings.Format("VscodeNotFolder", vscodeDir), distro, canonical);
            }

            if (IsLinkOrJunction(vscodeDir))
            {
                return DebugRefused(Strings.Format("VscodeIsLink", vscodeDir), distro, canonical);
            }
        }

        (string? name, string? error) = await ResolveDistroAsync(distro, ct);
        if (name is null)
        {
            return DebugRefused(error!, distro, canonical);
        }

        (string check, string readyKey, string missingKey, string hintKey) = BackendProbe(canonical);
        CommandResult probe = await RunBashAsync(name, check, BackendProbeTimeout, ct);
        bool backendReady = probe.Success && probe.StdOut.Contains("READY", StringComparison.Ordinal);
        string backendDetail = backendReady
            ? Strings.Get(readyKey)
            : probe.Success ? Strings.Get(missingKey) : Detail(probe);
        string? installHint = backendReady ? null : Strings.Get(hintKey);

        JsonObject config = BuildLaunchConfig(canonical, name, programPath)!;
        string configName = config["name"]!.GetValue<string>();

        bool written = false;
        if (write)
        {
            (written, string? writeError) = TryWriteLaunchConfig(vscodeDir!, config);
            if (writeError is not null)
            {
                return DebugRefused(writeError, name, canonical);
            }
        }

        return new WslDebugSetupResult(
            true, null, name, canonical, backendReady, backendDetail, installHint, configName, config, written);
    }

    /// <summary>The VS Code launch object for a canonical runtime (dotnet, python, node), or null for any other runtime.</summary>
    public static JsonObject? BuildLaunchConfig(string runtime, string distro, string programPath) => runtime switch
    {
        "dotnet" => new JsonObject
        {
            ["name"] = "WSL: .NET Launch",
            ["type"] = "coreclr",
            ["request"] = "launch",
            ["program"] = programPath,
            ["cwd"] = "${workspaceFolder}",
            ["stopAtEntry"] = false,
            ["console"] = "internalConsole",
            ["pipeTransport"] = new JsonObject
            {
                ["pipeCwd"] = "${workspaceFolder}",
                ["pipeProgram"] = "wsl.exe",
                ["pipeArgs"] = new JsonArray("-d", distro, "--"),
                ["debuggerPath"] = "~/.vsdbg/vsdbg",
            },
        },

        "python" => new JsonObject
        {
            ["name"] = "WSL: Python Launch",
            ["type"] = "debugpy",
            ["request"] = "launch",
            ["program"] = programPath,
            ["console"] = "integratedTerminal",
            ["python"] = "wsl.exe",
            ["pythonArgs"] = new JsonArray("-d", distro, "--", "python3"),
        },

        "node" => new JsonObject
        {
            ["name"] = "WSL: Node Launch",
            ["type"] = "node",
            ["request"] = "launch",
            ["program"] = programPath,
            ["cwd"] = "${workspaceFolder}",
            ["useWSL"] = true,
        },

        _ => null,
    };

    // ── distro resolution and listing ───────────────────────────────────────────

    /// <summary>Validates an explicit distro, or picks the default one from <c>wsl -l -v</c>.</summary>
    private async Task<(string? Name, string? Error)> ResolveDistroAsync(string? requested, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return IsValidDistroName(requested)
                ? (requested, null)
                : (null, Strings.Format("InvalidDistroName", requested));
        }

        CommandResult list = await ListAsync(ct);
        if (!list.Success)
        {
            return (null, Strings.Format("ListFailed", list.Launched ? list.ExitCode : -1, Detail(list)));
        }

        IReadOnlyList<WslDistro> distros = ParseDistroList(list.StdOut);
        if (distros.Count == 0)
        {
            return (null, Strings.Get("NoDistroRegistered"));
        }

        WslDistro? def = distros.FirstOrDefault(d => d.IsDefault);
        return def is null ? (null, Strings.Get("NoDefaultDistro")) : (def.Name, null);
    }

    private Task<CommandResult> ListAsync(CancellationToken ct) =>
        _commands.RunAsync(
            new CommandRequest
            {
                FileName = WslExe,
                Arguments = ["-l", "-v"],
                Environment = new Dictionary<string, string?> { ["WSL_UTF8"] = "1" },
                Timeout = ListTimeout,
                MaxOutputChars = OutputCapChars,
            },
            ct);

    private Task<CommandResult> RunBashAsync(string distro, string command, TimeSpan timeout, CancellationToken ct) =>
        _commands.RunAsync(
            new CommandRequest
            {
                FileName = WslExe,
                Arguments = ["-d", distro, "--", BashPath, "-c", command],
                Timeout = timeout,
                MaxOutputChars = OutputCapChars,
            },
            ct);

    /// <summary>
    ///     Parses <c>wsl -l -v</c> output. The command is run with WSL_UTF8=1, so the text is normally UTF-8. Older
    ///     wsl builds print UTF-16 LE; read as UTF-8 that shows up as a NUL after every character, and a UTF-16 BOM
    ///     shows up as U+FFFD. Both are removed here, so the same rows parse either way. CommandRunner always decodes
    ///     UTF-8, so the UTF-16 path is handled on the text, not on the raw bytes.
    /// </summary>
    private static IReadOnlyList<WslDistro> ParseDistroList(string text)
    {
        string cleaned = text
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Replace("﻿", string.Empty, StringComparison.Ordinal)
            .TrimStart('�');

        List<WslDistro> list = [];
        foreach (string line in cleaned.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            Match m = ListRow.Match(line);
            if (!m.Success || !int.TryParse(m.Groups[4].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
            {
                continue;
            }

            list.Add(new WslDistro(m.Groups[2].Value, m.Groups[1].Success, m.Groups[3].Value, version));
        }

        return list;
    }

    // ── debug helpers ───────────────────────────────────────────────────────────

    private static string? NormalizeRuntime(string? runtime)
    {
        string key = (runtime ?? string.Empty).Trim().ToLowerInvariant();
        return key is "dotnet" or "python" or "node" ? key : null;
    }

    private static (string Check, string ReadyKey, string MissingKey, string HintKey) BackendProbe(string runtime) => runtime switch
    {
        "dotnet" => (
            "test -x \"$HOME/.vsdbg/vsdbg\" && echo READY || echo MISSING",
            "DotNetReady",
            "DotNetMissing",
            "DotNetInstallHint"),

        "python" => (
            "python3 -c 'import debugpy' >/dev/null 2>&1 && echo READY || echo MISSING",
            "PythonReady",
            "PythonMissing",
            "PythonInstallHint"),

        _ => (
            "command -v node >/dev/null 2>&1 && echo READY || echo MISSING",
            "NodeReady",
            "NodeMissing",
            "NodeInstallHint"),
    };

    private static bool IsLinkOrJunction(string path) =>
        Directory.Exists(path) && new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>Creates launch.json only when it does not exist. An existing file is left alone and not reported as an error.</summary>
    private static (bool Written, string? Error) TryWriteLaunchConfig(string vscodeDir, JsonObject config)
    {
        string launchPath = Path.Combine(vscodeDir, "launch.json");
        if (File.Exists(launchPath))
        {
            return (false, null);
        }

        try
        {
            Directory.CreateDirectory(vscodeDir);
            JsonObject root = new()
            {
                ["version"] = "0.2.0",
                ["configurations"] = new JsonArray(config.DeepClone()),
            };

            using FileStream stream = new(launchPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(root.ToJsonString(Indented) + "\n");
            return (true, null);
        }
        catch (IOException) when (File.Exists(launchPath))
        {
            // Created between the check and the open: keep the other writer's file.
            return (false, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, Strings.Format("LaunchConfigWriteFailed", launchPath, ex.Message));
        }
    }

    // ── shared helpers ──────────────────────────────────────────────────────────

    private static WslRunResult RunRefused(string error, string? distro) =>
        new(false, error, distro ?? DefaultLabel, -1, "", "", false, false, null);

    private static WslDebugSetupResult DebugRefused(string error, string? distro, string? runtime) =>
        new(false, error, distro ?? DefaultLabel, runtime ?? string.Empty, false, null, null, null, null, false);

    private static Dictionary<string, string> ParseKeyValues(string text)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        foreach (string raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        return map;
    }

    private static (string Text, bool Capped) CapAndTrim(string text, bool alreadyCapped)
    {
        bool capped = alreadyCapped;
        if (text.Length > OutputCapChars)
        {
            text = text[..OutputCapChars];
            capped = true;
        }

        return (text.TrimEnd('\r', '\n'), capped);
    }

    private static string Detail(CommandResult r)
    {
        if (!r.Launched)
        {
            return r.LaunchError ?? string.Empty;
        }

        if (r.TimedOut)
        {
            return Strings.Get("ProbeTimedOut");
        }

        return string.IsNullOrWhiteSpace(r.StdErr)
            ? Strings.Format("ProbeExitCode", r.ExitCode)
            : r.StdErr.Trim();
    }
}
