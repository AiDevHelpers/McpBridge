using AiDevHelpers.Common.Processes;

namespace AiDevHelpers.Helpers.WslHelper;

/// <summary>Seam for running <c>wsl.exe</c> and other programs. Tests supply a fake; production uses <see cref="CommandRunnerWslCommands" />.</summary>
public interface IWslCommands
{
    Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default);
}

/// <summary>Default implementation: every call goes through <see cref="CommandRunner" />.</summary>
public sealed class CommandRunnerWslCommands : IWslCommands
{
    public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default) =>
        CommandRunner.RunAsync(request, ct);
}
