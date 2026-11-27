using AiDevHelpers.Common.Processes;

namespace AiDevHelpers.Helpers.ContainerHelper;

/// <summary>Seam for running runtime CLI commands. Tests supply a fake; production uses <see cref="CommandRunnerRuntimeCommands" />.</summary>
public interface IRuntimeCommands
{
    Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default);
}

/// <summary>Default implementation: every call goes through <see cref="CommandRunner" />.</summary>
public sealed class CommandRunnerRuntimeCommands : IRuntimeCommands
{
    public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default) =>
        CommandRunner.RunAsync(request, ct);
}
