using AiDevHelpers.Common;
using AiDevHelpers.Common.Processes;
using AiDevHelpers.Common.Terminals;

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
    string? TerminalNote);

/// <summary>
/// Runs a script from a repo with the interpreter that fits it, with a no-output watchdog and a timeout, and records the run so a
/// script that fails repeatedly or hangs is noticed. The script must be a file inside the repo; arguments are passed as data, with no
/// shell in between. The seams keep it testable without starting processes.
/// </summary>
public sealed class ScriptRunService(
    ScriptRunTracker tracker,
    ITerminalProbe probe,
    Func<TerminalChoices> choices,
    Func<CommandRequest, CancellationToken, Task<CommandResult>> run)
{
    private const int TailChars = 4000;

    public async Task<ScriptRunReport> RunAsync(
        string repoPath,
        string script,
        IReadOnlyList<string> args,
        ScriptWatchOptions serverOptions,
        CancellationToken ct)
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
            };
        }

        var options = serverOptions.WithUserOverride(root);
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
