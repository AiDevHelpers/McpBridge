namespace AiDevHelpers.Common.Contracts;

/// <summary>What a script run produced. TimedOut means the run was killed at the limit.</summary>
public sealed record ScriptRunResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

/// <summary>Runs one script file with an argument array (no shell) and a time limit.</summary>
public interface IScriptRunner
{
    ScriptRunResult Run(string scriptPath, IReadOnlyList<string> args, TimeSpan timeout);
}
