using System.ComponentModel;
using System.Diagnostics;
using AiDevHelpers.Helpers.LogHelper.Contracts;

namespace AiDevHelpers.Helpers.LogHelper.Sources;

/// <summary>Runs a process with <see cref="ProcessStartInfo.ArgumentList"/>, so no argument is ever parsed by a shell.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<string> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using Process process = new();
        process.StartInfo = info;
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new IOException(Strings.Format("ProcessNotStarted", file, ex.Message), ex);
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        string output = await stdout;

        if (process.ExitCode != 0)
        {
            throw new IOException(Strings.Format("ProcessExited", file, process.ExitCode, stderr.Trim()));
        }

        return output;
    }
}
