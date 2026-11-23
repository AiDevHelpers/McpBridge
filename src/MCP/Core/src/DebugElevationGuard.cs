using AiDevHelpers.Bridge;
using AiDevHelpers.Common;
using AiDevHelpers.Common.Processes;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// Stops "start debugging" on a Visual Studio instance that is not elevated when the solution needs administrator rights: the debug run
/// would fail or silently run without them. The refusal tells the AI to ask the user and relaunch elevated; it never relaunches by itself.
/// It only acts when exactly one Visual Studio instance matches, so an ambiguous target is left to the router's own error.
/// </summary>
internal static class DebugElevationGuard
{
    public static string? Check(string? solutionPath, string? ide, int? instancePid) =>
        Check(solutionPath, ide, instancePid, () => VsInstanceRegistry.ListRunning(), ElevationPolicy.Decide, ProcessElevation.Of);

    internal static string? Check(
        string? solutionPath,
        string? ide,
        int? instancePid,
        Func<IReadOnlyList<VsInstance>> running,
        Func<string, LaunchDecision> decide,
        Func<int, ElevationState> stateOf)
    {
        if (ide is not null && !string.Equals(ide, "VisualStudio", StringComparison.OrdinalIgnoreCase))
            return null;

        var candidates = running().Where(i => string.Equals(i.Ide, "VisualStudio", StringComparison.OrdinalIgnoreCase)).ToList();
        if (instancePid is { } pid)
            candidates = [.. candidates.Where(i => i.Pid == pid)];
        else if (!string.IsNullOrWhiteSpace(solutionPath))
            candidates = [.. candidates.Where(i => i.SolutionPath is not null && string.Equals(Path.GetFullPath(i.SolutionPath), Path.GetFullPath(solutionPath), StringComparison.OrdinalIgnoreCase))];

        if (candidates.Count != 1 || candidates[0].SolutionPath is not { } solution)
            return null;

        var decision = decide(solution);
        return decision.Elevated && stateOf(candidates[0].Pid) == ElevationState.NotElevated
            ? Strings.Format("IdeDebugNeedsElevated", decision.Reason, candidates[0].Pid)
            : null;
    }
}
