using AiDevHelpers.Common;
using AiDevHelpers.Common.Processes;
using AiDevHelpers.Common.Terminals;
using AiDevHelpers.MCP.Core.Tools;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>The interpreter a run used, as the caller sees it.</summary>
public sealed record InterpreterInfo(string Name, string? Path, string? Version, string Reason);

/// <summary>
/// The outcome of <see cref="ScriptRunService.RunAsync"/>. Error is set when nothing ran. Advice is set when the script looks stuck, and
/// TerminalNote when the interpreter's terminal type is installed but not registered.
/// </summary>
public sealed record ScriptRunReport(
    bool Ok,
    string? Error,
    int? ExitCode,
    bool TimedOut,
    bool Silent,
    string? StdOut,
    string? StdErr,
    InterpreterInfo? Interpreter,
    string? InstallHint,
    ScriptFlag Flag,
    string? Advice,
    string? TerminalNote,
    bool Interactive = false,
    string? SessionId = null,
    string? TabId = null,
    string? InstallOffer = null);

/// <summary>
/// Runs a script from a repo with the interpreter that fits it, with a no-output watchdog and a timeout, and records the run so a
/// script that fails repeatedly or hangs is noticed. The script must be a file inside the repo; arguments are passed as data, with no
/// shell in between. The seams keep it testable without starting processes.
/// </summary>
public sealed class ScriptRunService(
    ScriptRunTracker tracker,
    ITerminalProbe probe,
    Func<TerminalChoices> choices,
    Func<CommandRequest, CancellationToken, Task<CommandResult>> run,
    Func<InteractiveRequest, CancellationToken, Task<InteractiveResult>>? interactiveRun = null,
    Func<string, string, IReadOnlyList<string>, CancellationToken, Task<ElevRunResult>>? elevatedVisibleRun = null)
{
    private const int TailChars = 4000;

    public async Task<ScriptRunReport> RunAsync(
        string repoPath,
        string script,
        IReadOnlyList<string> args,
        ScriptWatchOptions serverOptions,
        CancellationToken ct,
        bool interactive = false,
        bool elevated = false)
    {
        var root = Directory.Exists(repoPath) ? RepoSettingsFile.FindRepoRootFromFolder(repoPath) : null;
        if (root is null)
            return Failed(Strings.Get("ScriptRunNeedsRepo"));

        var full = Path.GetFullPath(Path.IsPathRooted(script) ? script : Path.Combine(repoPath, script));
        var relative = Path.GetRelativePath(root, full);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) || !File.Exists(full))
            return Failed(Strings.Get("ScriptRunOutsideRepo"));

        var overrides = ScriptInterpreterResolver.ReadOverrides(root);
        var interpreter = ScriptInterpreterResolver.Resolve(probe, full, root, overrides, out var unsupported);
        if (interpreter is null)
            return Failed(Strings.Format("ScriptRunUnsupported", unsupported!.Reason));

        var info = new InterpreterInfo(interpreter.Name, interpreter.Path, interpreter.Version, interpreter.Reason);
        if (!interpreter.Installed)
        {
            return Failed(Strings.Format("ScriptRunMissingInterpreter", interpreter.Name, interpreter.InstallHint)) with
            {
                Interpreter = info,
                InstallHint = interpreter.InstallHint,
                InstallOffer = ToolInstallService.OfferFor(interpreter.Name, choices()),
            };
        }

        var options = serverOptions.WithUserOverride(root);
        if (interactive && elevated)
            return await RunElevatedInWindowAsync(interpreter, info, relative, root, args, options, ct);

        if (interactive)
            return await RunInTabAsync(interpreter, info, full, relative, root, args, options, ct);

        var result = await run(
            new CommandRequest
            {
                FileName = interpreter.Path!,
                Arguments = interpreter.BuildArguments(full, args),
                WorkingDirectory = root,
                Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
                NoOutputTimeout = TimeSpan.FromSeconds(options.NoOutputSeconds),
            },
            ct);

        if (!result.Launched)
            return Failed(result.LaunchError ?? Strings.Get("ScriptRunNeedsRepo")) with { Interpreter = info };

        var key = ScriptRunTracker.Key(root, relative, args);
        tracker.Record(key, new ScriptRunOutcome(result.ExitCode, result.TimedOut, result.Silent));
        var assessment = tracker.Assess(key, options);

        return new ScriptRunReport(
            result.Success,
            null,
            result.ExitCode,
            result.TimedOut,
            result.Silent,
            Tail(result.StdOut),
            Tail(result.StdErr),
            info,
            null,
            assessment.Flag,
            assessment.Flag == ScriptFlag.None ? null : Strings.Format("ScriptRunAdvice", assessment.Reason),
            TerminalNote(interpreter));
    }

    // Runs an approved script elevated in a visible window, through the broker. The broker checks the allowlist, fingerprint and approval; this
    // only asks. A script that is not approved comes back as a refusal, never as a run. PowerShell scripts only: the broker runs pwsh.
    private async Task<ScriptRunReport> RunElevatedInWindowAsync(
        ResolvedInterpreter interpreter,
        InterpreterInfo info,
        string relative,
        string root,
        IReadOnlyList<string> args,
        ScriptWatchOptions options,
        CancellationToken ct)
    {
        if (elevatedVisibleRun is null)
            return Failed(Strings.Get("ScriptRunInteractiveUnavailable")) with { Interpreter = info };

        if (interpreter.Name != "pwsh" && interpreter.Name != "powershell")
            return Failed(Strings.Get("ScriptRunElevatedPowerShellOnly")) with { Interpreter = info };

        var result = await elevatedVisibleRun(root, relative.Replace('\\', '/'), args, ct);
        var key = ScriptRunTracker.Key(root, relative, args);
        tracker.Record(key, new ScriptRunOutcome(result.ExitCode, TimedOut: false, Silent: false));
        var assessment = tracker.Assess(key, options);
        return new ScriptRunReport(
            result.ExitCode == 0,
            result.ExitCode == 0 ? null : Strings.Format("InstallFailed", result.ExitCode, Tail(result.Output)),
            result.ExitCode,
            false,
            false,
            Tail(result.Output),
            string.Empty,
            info,
            null,
            assessment.Flag,
            assessment.Flag == ScriptFlag.None ? null : Strings.Format("ScriptRunAdvice", assessment.Reason),
            TerminalNote(interpreter),
            Interactive: true);
    }

    // Runs the script in a visible terminal tab. It is recorded like any other run, so a script that still fails is still counted.
    private async Task<ScriptRunReport> RunInTabAsync(
        ResolvedInterpreter interpreter,
        InterpreterInfo info,
        string full,
        string relative,
        string root,
        IReadOnlyList<string> args,
        ScriptWatchOptions options,
        CancellationToken ct)
    {
        if (interactiveRun is null)
            return Failed(Strings.Get("ScriptRunInteractiveUnavailable")) with { Interpreter = info };

        var request = new InteractiveRequest(
            interpreter.Path!,
            interpreter.BuildArguments(full, args, interactive: true),
            root,
            Path.GetFileName(full),
            TimeSpan.FromSeconds(Math.Max(600, options.TimeoutSeconds * 3)));
        var tab = await interactiveRun(request, ct);

        var key = ScriptRunTracker.Key(root, relative, args);
        tracker.Record(key, new ScriptRunOutcome(tab.ExitCode, TimedOut: false, Silent: false));
        var assessment = tracker.Assess(key, options);
        return new ScriptRunReport(
            tab.Success && tab.ExitCode == 0,
            tab.Errors is { Count: > 0 } ? string.Join("; ", tab.Errors) : null,
            tab.ExitCode,
            false,
            false,
            Tail(tab.Stdout ?? string.Empty),
            Tail(tab.Stderr ?? string.Empty),
            info,
            null,
            assessment.Flag,
            assessment.Flag == ScriptFlag.None ? null : Strings.Format("ScriptRunAdvice", assessment.Reason),
            TerminalNote(interpreter),
            Interactive: true,
            SessionId: tab.SessionId,
            TabId: tab.TabId);
    }

    // The advice for a run that did not go through script_run (an elev run), or null when the script is not flagged.
    public static string? AdviceFor(ScriptAssessment assessment) =>
        assessment.Flag == ScriptFlag.None ? null : Strings.Format("ScriptRunAdvice", assessment.Reason);

    private string? TerminalNote(ResolvedInterpreter interpreter)
    {
        if (interpreter.TerminalTypeId is not { } type || choices().Registered.Any(r => string.Equals(r.Id, type, StringComparison.OrdinalIgnoreCase)))
            return null;

        return Strings.Format("ScriptRunTerminalNote", type);
    }

    private static ScriptRunReport Failed(string error) =>
        new(false, error, null, false, false, null, null, null, null, ScriptFlag.None, null, null);

    private static string Tail(string text) => text.Length <= TailChars ? text : text[^TailChars..];
}
