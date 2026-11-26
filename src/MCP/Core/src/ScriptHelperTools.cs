using System.ComponentModel;
using System.Text;
using AiDevHelpers.Common.Processes;
using AiDevHelpers.MCP.Core.Services;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>The outcome of one elev run: its exit code and the combined output it printed.</summary>
public sealed record ElevRunResult(int ExitCode, string Output);

/// <summary>Runs an approved script through elev.exe. The seam keeps the tool testable without elevation.</summary>
public interface IElevRunner
{
    Task<ElevRunResult> RunAsync(string projectRoot, string script, IReadOnlyList<string> args, CancellationToken ct);
}

/// <summary>
/// Asks the broker, through elev.exe, which of a repo's allowlisted files this user has not approved. Read only; exit 0 means all are
/// approved, 1 some are not (the output is JSON), 2 the broker did not answer.
/// </summary>
public interface IElevVerifier
{
    Task<ElevRunResult> VerifyAsync(string projectRoot, CancellationToken ct);
}

/// <summary>
/// Scans a repo for projects that need elevation through <c>elev --repo-scan</c>. Local and read only: it needs no broker and
/// writes nothing. The output is the scan as JSON.
/// </summary>
public interface IElevScanner
{
    Task<ElevRunResult> ScanAsync(string repoRoot, CancellationToken ct);

    /// <summary>Applies the person's decisions (JSON) through elev repo-apply. The JSON must hold only names the scan reported.</summary>
    Task<ElevRunResult> ApplyAsync(string repoRoot, string decisionsJson, CancellationToken ct);
}

/// <summary>
/// Runs an approved script elevated in a visible console window through the broker, so the person sees its prompts and errors. The same
/// allowlist, fingerprint and approval checks as <see cref="IElevRunner"/> apply; the transcript comes back as the output.
/// </summary>
public interface IElevVisibleRunner
{
    Task<ElevRunResult> RunVisibleAsync(string projectRoot, string script, IReadOnlyList<string> args, CancellationToken ct);
}

/// <summary>
/// Runs the installer script the admin approved with "setup tools", through the broker, for one tool name from the built-in table.
/// The name is checked again by elev.exe and by the script itself; nothing the caller supplies reaches winget except that name.
/// </summary>
public interface IElevInstaller
{
    Task<ElevRunResult> InstallAsync(string toolName, CancellationToken ct);

    /// <summary>Runs install-workload through elev.exe. The name is checked again by elev.exe and by the script.</summary>
    Task<ElevRunResult> InstallWorkloadAsync(string workloadName, CancellationToken ct);
}

/// <summary>Starts elev.exe from the MCP server's folder and collects what it prints.</summary>
internal sealed class ProcessElevRunner : IElevRunner, IElevVerifier, IElevScanner, IElevInstaller, IElevVisibleRunner
{
    public Task<ElevRunResult> RunVisibleAsync(string projectRoot, string script, IReadOnlyList<string> args, CancellationToken ct) =>
        RunElevAsync(["run", "--visible", projectRoot, script, .. args], ct);

    public Task<ElevRunResult> InstallAsync(string toolName, CancellationToken ct) =>
        RunElevAsync(["install-tool", toolName], ct);

    public Task<ElevRunResult> InstallWorkloadAsync(string workloadName, CancellationToken ct) =>
        RunElevAsync(["install-workload", workloadName], ct);

    public Task<ElevRunResult> ScanAsync(string repoRoot, CancellationToken ct) =>
        RunElevAsync(["repo-scan", repoRoot], ct);

    public Task<ElevRunResult> ApplyAsync(string repoRoot, string decisionsJson, CancellationToken ct) =>
        RunElevAsync(["repo-apply", repoRoot, decisionsJson], ct);

    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);

    public Task<ElevRunResult> RunAsync(string projectRoot, string script, IReadOnlyList<string> args, CancellationToken ct) =>
        RunElevAsync(["run", projectRoot, script, .. args], ct);

    public Task<ElevRunResult> VerifyAsync(string projectRoot, CancellationToken ct) =>
        RunElevAsync(["verify", projectRoot], ct);

    private static async Task<ElevRunResult> RunElevAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "elev.exe");
        if (!File.Exists(exe))
        {
            throw new InvalidOperationException(Strings.Format("ElevNotFound", exe));
        }

        var result = await CommandRunner.RunAsync(new CommandRequest { FileName = exe, Arguments = arguments, Timeout = Limit }, ct);
        if (!result.Launched)
        {
            throw new InvalidOperationException(Strings.Get("ElevDidNotStart"));
        }

        if (result.TimedOut)
        {
            throw new OperationCanceledException(Strings.Get("ElevTimedOut"));
        }

        return new ElevRunResult(result.ExitCode, result.StdOut + result.StdErr);
    }
}

/// <summary>
///     MCP surface for Script Helper: runs an allowlisted script elevated through the running broker.
///     The script must be listed in the project's .elevated.json and approved by a person.
/// </summary>
[McpServerToolType]
internal sealed class ScriptHelperTools
{
    private readonly IElevRunner _runner;
    private readonly ScriptRunTracker? _tracker;
    private readonly ScriptWatchOptions _watch;

    public ScriptHelperTools(IElevRunner runner, ScriptRunTracker? tracker = null, Microsoft.Extensions.Options.IOptions<ScriptWatchOptions>? watch = null)
    {
        _runner = runner;
        _tracker = tracker;
        _watch = watch?.Value ?? new ScriptWatchOptions();
    }

    [McpServerTool]
    [Description(
        "Run an approved debug script elevated through the Script Helper broker. projectRoot is the folder with " +
        "the .elevated.json manifest; script is the path relative to it; args are passed to the script in order. " +
        "Returns the exit code and output, or a refusal reason.")]
    public async Task<string> RunElevatedScript(
        string projectRoot,
        string script,
        string[]? args = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(script))
        {
            return Strings.Get("ScriptHelperNeedsArgs");
        }

        try
        {
            var result = await _runner.RunAsync(projectRoot, script, args ?? Array.Empty<string>(), ct);
            var text = new StringBuilder();
            text.Append("exit ").Append(result.ExitCode).AppendLine();
            text.Append(result.Output);

            // An elevated run feeds the same tracker as script_run, so a script that keeps failing is noticed here too.
            if (_tracker is not null)
            {
                var key = ScriptRunTracker.Key(projectRoot, script, args ?? Array.Empty<string>());
                _tracker.Record(key, new ScriptRunOutcome(result.ExitCode, TimedOut: false, Silent: false));
                if (ScriptRunService.AdviceFor(_tracker.Assess(key, _watch)) is { } advice)
                {
                    text.AppendLine().Append(advice);
                }
            }

            return text.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Strings.Format("ScriptHelperError", ex.Message);
        }
    }
}
