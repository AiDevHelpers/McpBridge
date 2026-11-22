using System.Diagnostics;
using System.Text;
using AiDevHelpers.Common.Contracts;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// Runs a script with pwsh: no shell, arguments passed as an argument list, output capped,
/// no console window, and the whole process tree killed at the time limit.
/// </summary>
public sealed class ProcessScriptRunner : IScriptRunner
{
    public const int MaxOutputChars = 64 * 1024;
    private const string Marker = "\n[output truncated]";
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    public ScriptRunResult Run(string scriptPath, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("pwsh.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = Drain(process.StandardOutput);
        var stderr = Drain(process.StandardError);

        var timedOut = !process.WaitForExit(timeout);
        if (timedOut)
        {
            KillTree(process);
        }

        // Drain tasks finish once the pipes close; bound the wait so a stuck pipe cannot hang the broker.
        var drained = Task.WaitAll(new Task[] { stdout, stderr }, DrainGrace);
        var exitCode = timedOut ? -1 : process.ExitCode;

        if (!drained)
        {
            return new ScriptRunResult(exitCode, string.Empty,
                "output pipes did not close in time; output discarded", timedOut);
        }

        return new ScriptRunResult(exitCode, stdout.Result, stderr.Result, timedOut);
    }

    private static Task<string> Drain(StreamReader reader) => Task.Run(() =>
    {
        var text = new StringBuilder();
        var truncated = false;
        var buffer = new char[4096];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            var room = MaxOutputChars - text.Length;
            if (room > 0)
            {
                text.Append(buffer, 0, Math.Min(room, read));
            }

            if (read > room)
            {
                truncated = true;
            }
        }

        return truncated ? text.ToString() + Marker : text.ToString();
    });

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }

        process.WaitForExit(5000);
    }
}
