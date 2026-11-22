using AiDevHelpers.Common.Processes;

namespace AiDevHelpers.Helpers.ExtensionsHelper;

/// <summary>Runs a command without a shell. Tests replace it with a recorder.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default);
}

/// <summary>Real runner: delegates to the shared Common runner, so no argument is shell-parsed.</summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        // VS Code's CLI is a .cmd shim on Windows.
        var exe = file == "code" && OperatingSystem.IsWindows() ? "code.cmd" : file;

        var result = await CommandRunner.RunAsync(new CommandRequest { FileName = exe, Arguments = args }, ct);
        if (!result.Launched)
        {
            throw new InvalidOperationException(Strings.Format("CouldNotStartProcess", exe));
        }

        return new CommandResult(result.ExitCode, result.StdOut, result.StdErr);
    }
}
