using AiDevHelpers.Common.Processes;

namespace AiDevHelpers.Helpers.ProjectHelper;

/// <summary>
/// Runs one command to completion. Every <c>dotnet</c> call in this helper goes through this delegate, so tests substitute a fake.
/// It reuses Common's <see cref="CommandRunner"/>; no interface of its own.
/// </summary>
public delegate Task<CommandResult> RunCommand(CommandRequest request, CancellationToken ct);

public static class CommandSeam
{
    /// <summary>The real runner.</summary>
    public static readonly RunCommand Default = CommandRunner.RunAsync;
}
