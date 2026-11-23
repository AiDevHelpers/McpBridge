using System.Diagnostics;
using System.Text.Json;
using AiDevHelpers.Common.Contracts;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// Runs an approved script in a normal, visible pwsh window. The broker is started hidden, so the window is opened through the shell, which
/// gives the child its own console. A fixed wrapper (never built from caller text) reads the arguments from a JSON file, runs the script
/// under a transcript, and holds the window open after a failure so the error can be read. The transcript comes back as the output.
/// </summary>
public sealed class ProcessVisibleRunner : IVisibleScriptRunner
{
    public const int MaxOutputChars = 64 * 1024;

    /// <summary>The wrapper that runs inside the window. It contains no caller input.</summary>
    public const string Wrapper = """
        param([string]$Script, [string]$ArgsFile, [string]$Log)
        Start-Transcript -Path $Log -Force | Out-Null
        $scriptArgs = @(Get-Content -Raw -LiteralPath $ArgsFile | ConvertFrom-Json)
        & $Script @scriptArgs
        $code = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } elseif ($?) { 0 } else { 1 }
        Stop-Transcript | Out-Null
        if ($code -ne 0) { Write-Host "The script exited with code $code. Press Enter to close this window."; [void](Read-Host) }
        exit $code
        """;

    /// <summary>The argument line that starts the wrapper. Every path is one the broker made, so none can hold a quote.</summary>
    public static string WrapperArguments(string wrapper, string script, string argsFile, string log) =>
        $"-NoProfile -ExecutionPolicy Bypass -File \"{wrapper}\" -Script \"{script}\" -ArgsFile \"{argsFile}\" -Log \"{log}\"";

    public ScriptRunResult RunVisible(string scriptPath, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var work = Path.Combine(Path.GetDirectoryName(scriptPath)!, ".visible-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var wrapper = Path.Combine(work, "wrapper.ps1");
        var argsFile = Path.Combine(work, "args.json");
        var log = Path.Combine(work, "transcript.txt");
        File.WriteAllText(wrapper, Wrapper);
        File.WriteAllText(argsFile, JsonSerializer.Serialize(args));

        var info = new ProcessStartInfo("pwsh.exe")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
            Arguments = WrapperArguments(wrapper, scriptPath, argsFile, log),
        };

        using var process = Process.Start(info)!;
        var timedOut = !process.WaitForExit(timeout);
        if (timedOut)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        }

        var text = File.Exists(log) ? Tail(File.ReadAllText(log)) : string.Empty;
        return new ScriptRunResult(timedOut ? -1 : process.ExitCode, text, string.Empty, timedOut);
    }

    private static string Tail(string text) => text.Length <= MaxOutputChars ? text : "[output truncated]\n" + text[^MaxOutputChars..];
}
