using System.Globalization;
using System.Text.RegularExpressions;
using AiDevHelpers.Common.Processes;

namespace AiDevHelpers.Helpers.ContainerHelper;

/// <summary>
///     Container status, logs, start, stop, restart and exec over Docker or Podman. Refusals and failures come back
///     as <c>Ok = false</c> with <c>Error</c> text; ordinary failures are never thrown.
/// </summary>
public sealed class ContainerService
{
    public const int OutputCapChars = 20_000;

    private const string InspectFormat =
        "{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{end}}|{{.State.ExitCode}}|{{.State.FinishedAt}}";

    private static readonly string[] KnownRuntimes = ["docker", "podman"];
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LogsTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromMinutes(2);
    private static readonly Regex ErrorLine = new("error|exception|fail|fatal|panic", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IRuntimeCommands _commands;

    public ContainerService()
        : this(new CommandRunnerRuntimeCommands())
    {
    }

    public ContainerService(IRuntimeCommands commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands = commands;
    }

    /// <summary>Resolves the runtime: the requested one if given, else docker, then podman.</summary>
    public async Task<RuntimeDetection> DetectAsync(string? requested, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            string? runtime = NormalizeRuntime(requested, out string? error);
            if (runtime is null)
            {
                return new RuntimeDetection(null, error);
            }

            CommandResult probe = await RunAsync(runtime, ["--version"], ProbeTimeout, ct);
            return probe.Success
                ? new RuntimeDetection(runtime, null)
                : new RuntimeDetection(null, Strings.Format("RuntimeNotInstalled", runtime));
        }

        foreach (string runtime in KnownRuntimes)
        {
            CommandResult probe = await RunAsync(runtime, ["--version"], ProbeTimeout, ct);
            if (probe.Success)
            {
                return new RuntimeDetection(runtime, null);
            }
        }

        return new RuntimeDetection(null, Strings.Get("RuntimeNotFound"));
    }

    public Task<ContainerStatusResult> StatusAsync(string name, string? runtime = null, CancellationToken ct = default) =>
        PreflightAsync(name, runtime, ct);

    public async Task<ContainerLogsResult> LogsAsync(
        string name,
        int tailLines = 500,
        bool errorLinesOnly = false,
        string? runtime = null,
        CancellationToken ct = default)
    {
        if (tailLines < 1)
        {
            return new ContainerLogsResult(false, Strings.Get("TailLinesInvalid"), null, name, 0, "", false);
        }

        ContainerStatusResult pre = await PreflightAsync(name, runtime, ct);
        if (!pre.Ok)
        {
            return new ContainerLogsResult(false, pre.Error, pre.Runtime, name, 0, "", false);
        }

        string rt = pre.Runtime!;
        CommandResult r = await RunAsync(
            rt,
            ["logs", "--tail", tailLines.ToString(CultureInfo.InvariantCulture), "--", name],
            LogsTimeout,
            ct);
        if (!r.Success)
        {
            return new ContainerLogsResult(false, Strings.Format("ActionFailed", rt, "logs", name, ExitOf(r), Detail(r)), rt, name, 0, "", false);
        }

        string text = Combine(r.StdOut, r.StdErr);
        bool capped = r.StdOutTruncated || r.StdErrTruncated;
        if (errorLinesOnly)
        {
            text = string.Concat(text.Split('\n').Where(line => ErrorLine.IsMatch(line)).Select(line => line + "\n"));
        }

        if (text.Length > OutputCapChars)
        {
            text = text[..OutputCapChars];
            capped = true;
        }

        return new ContainerLogsResult(true, null, rt, name, CountLines(text), text, capped);
    }

    public Task<ContainerActionResult> StartAsync(string name, string? runtime = null, CancellationToken ct = default) =>
        ActionAsync("start", name, runtime, ct);

    public Task<ContainerActionResult> StopAsync(string name, string? runtime = null, CancellationToken ct = default) =>
        ActionAsync("stop", name, runtime, ct);

    public Task<ContainerActionResult> RestartAsync(string name, string? runtime = null, CancellationToken ct = default) =>
        ActionAsync("restart", name, runtime, ct);

    public async Task<ContainerExecResult> ExecAsync(
        string name,
        string command,
        int timeoutSeconds = 60,
        string? runtime = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return ExecFail(Strings.Get("CommandRequired"), null, name);
        }

        if (timeoutSeconds < 1 || timeoutSeconds > 600)
        {
            return ExecFail(Strings.Get("TimeoutInvalid"), null, name);
        }

        ContainerStatusResult pre = await PreflightAsync(name, runtime, ct);
        if (!pre.Ok)
        {
            return ExecFail(pre.Error!, pre.Runtime, name);
        }

        string rt = pre.Runtime!;
        CommandResult r = await RunAsync(
            rt,
            ["exec", name, "/bin/sh", "-c", command],
            TimeSpan.FromSeconds(timeoutSeconds),
            ct);
        if (!r.Launched)
        {
            return ExecFail(Strings.Format("LaunchFailed", rt, r.LaunchError), rt, name);
        }

        bool capped = r.StdOutTruncated || r.StdErrTruncated || r.StdOut.Length > OutputCapChars || r.StdErr.Length > OutputCapChars;
        string stdout = Cut(r.StdOut);
        string stderr = Cut(r.StdErr);
        if (r.TimedOut)
        {
            return new ContainerExecResult(false, Strings.Format("ExecTimedOut", timeoutSeconds), rt, name, -1, stdout, stderr, true, capped);
        }

        return new ContainerExecResult(true, null, rt, name, r.ExitCode, stdout, stderr, false, capped);
    }

    private async Task<ContainerActionResult> ActionAsync(string verb, string name, string? requestedRuntime, CancellationToken ct)
    {
        ContainerStatusResult pre = await PreflightAsync(name, requestedRuntime, ct);
        if (!pre.Ok)
        {
            return new ContainerActionResult(false, pre.Error, pre.Runtime, name, verb, null);
        }

        string rt = pre.Runtime!;
        CommandResult r = await RunAsync(rt, [verb, "--", name], ActionTimeout, ct);
        if (!r.Success)
        {
            return new ContainerActionResult(false, Strings.Format("ActionFailed", rt, verb, name, ExitOf(r), Detail(r)), rt, name, verb, null);
        }

        ContainerStatusResult after = await PreflightAsync(name, rt, ct);
        return new ContainerActionResult(after.Ok, after.Ok ? null : after.Error, rt, name, verb, after);
    }

    /// <summary>Name check, runtime CLI check, daemon check, then inspect. Runs before every action.</summary>
    private async Task<ContainerStatusResult> PreflightAsync(string name, string? requestedRuntime, CancellationToken ct)
    {
        if (!ContainerName.IsValid(name))
        {
            return StatusFail(Strings.Format("InvalidName", name), null, name);
        }

        string runtime;
        if (string.IsNullOrWhiteSpace(requestedRuntime))
        {
            // Detection probes each CLI with --version, which is the CLI check.
            RuntimeDetection detection = await DetectAsync(null, ct);
            if (!detection.Ok)
            {
                return StatusFail(detection.Error!, null, name);
            }

            runtime = detection.Runtime!;
        }
        else
        {
            string? normalized = NormalizeRuntime(requestedRuntime, out string? error);
            if (normalized is null)
            {
                return StatusFail(error!, null, name);
            }

            runtime = normalized;
            CommandResult cli = await RunAsync(runtime, ["--version"], ProbeTimeout, ct);
            if (!cli.Launched)
            {
                return StatusFail(Strings.Format("RuntimeNotInstalled", runtime), runtime, name);
            }

            if (!cli.Success)
            {
                return StatusFail(Strings.Format("CliFailed", runtime, ExitOf(cli), Detail(cli)), runtime, name);
            }
        }

        CommandResult daemon = await RunAsync(runtime, DaemonArgs(runtime), ProbeTimeout, ct);
        if (!daemon.Success)
        {
            string key = runtime == "docker" ? "DockerDaemonDown" : "PodmanDaemonDown";
            return StatusFail(Strings.Format(key, Detail(daemon)), runtime, name);
        }

        CommandResult inspect = await RunAsync(runtime, ["inspect", "-f", InspectFormat, "--", name], ProbeTimeout, ct);
        if (!inspect.Success)
        {
            return StatusFail(Strings.Format("ContainerNotFound", name, Detail(inspect)), runtime, name);
        }

        return ParseInspect(runtime, name, inspect.StdOut);
    }

    private static ContainerStatusResult ParseInspect(string runtime, string name, string output)
    {
        string[] parts = output.Trim().Split('|');
        if (parts.Length != 4)
        {
            return StatusFail(Strings.Format("StateUnreadable", name), runtime, name);
        }

        string health = parts[1].Trim();
        if (health.Length == 0 || health == "<nil>")
        {
            health = "none";
        }

        int? exitCode = int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code) ? code : null;
        return new ContainerStatusResult(true, null, runtime, name, parts[0].Trim(), health, exitCode, parts[3].Trim());
    }

    private static string? NormalizeRuntime(string requested, out string? error)
    {
        string name = requested.Trim().ToLowerInvariant();
        if (Array.IndexOf(KnownRuntimes, name) >= 0)
        {
            error = null;
            return name;
        }

        error = Strings.Format("RuntimeUnknown", requested.Trim());
        return null;
    }

    private static string[] DaemonArgs(string runtime)
    {
        if (runtime == "docker")
        {
            return ["info", "--format", "{{.ServerVersion}}"];
        }

        return ["info", "--format", "{{.Version.Version}}"];
    }

    private Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct) =>
        _commands.RunAsync(new CommandRequest { FileName = fileName, Arguments = arguments, Timeout = timeout }, ct);

    private static ContainerStatusResult StatusFail(string error, string? runtime, string name) =>
        new(false, error, runtime, name, null, null, null, null);

    private static ContainerExecResult ExecFail(string error, string? runtime, string name) =>
        new(false, error, runtime, name, -1, "", "", false, false);

    private static int ExitOf(CommandResult r) => r.Launched ? r.ExitCode : -1;

    private static string Detail(CommandResult r)
    {
        if (!r.Launched)
        {
            return r.LaunchError ?? "";
        }

        return (string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr).Trim();
    }

    private static string Combine(string first, string second)
    {
        if (first.Length > 0 && second.Length > 0 && !first.EndsWith('\n'))
        {
            return first + "\n" + second;
        }

        return first + second;
    }

    private static int CountLines(string text) => text.Split('\n').Count(line => line.Trim().Length > 0);

    private static string Cut(string text) => text.Length > OutputCapChars ? text[..OutputCapChars] : text;
}
