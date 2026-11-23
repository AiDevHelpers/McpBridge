namespace AiDevHelpers.Common.Contracts;

/// <summary>
/// Runs one script file in a visible console window, so the person sees its prompts and any errors it does not report, and returns the
/// transcript. Arguments are passed as data. The run is stopped at the time limit.
/// </summary>
public interface IVisibleScriptRunner
{
    ScriptRunResult RunVisible(string scriptPath, IReadOnlyList<string> args, TimeSpan timeout);
}
