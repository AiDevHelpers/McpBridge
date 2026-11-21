using System.Diagnostics;

namespace AiDevHelpers.Helpers.ExtensionsHelper;

/// <summary>Runs a command without a shell. Tests replace it with a recorder.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default);
}

/// <summary>Real runner: starts the process with its arguments as a list, so no argument is shell-parsed.</summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        // VS Code's CLI is a .cmd shim on Windows.
        var exe = file == "code" && OperatingSystem.IsWindows() ? "code.cmd" : file;

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException(Strings.Format("CouldNotStartProcess", exe));
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new CommandResult(process.ExitCode, await stdout, await stderr);
    }
}
