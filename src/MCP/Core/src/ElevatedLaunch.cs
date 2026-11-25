using System.ComponentModel;
using System.Diagnostics;
using AiDevHelpers.Common;
using AiDevHelpers.Common.Processes;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>What launching a solution should do, given the policy decision and whether an instance already has it open.</summary>
public enum LaunchAction
{
    StartNormal,
    StartElevated,

    /// <summary>An instance is already open and is as elevated as it needs to be.</summary>
    Reuse,

    /// <summary>The open instance is not elevated but needs to be. Closing it can lose unsaved work, so the person is asked first.</summary>
    AskRelaunch,

    /// <summary>The person agreed: save and close the open instance, then start one elevated.</summary>
    RelaunchElevated,
}

/// <summary>Launching Visual Studio elevated. One UAC prompt per launch; the broker is not involved, because it only runs approved scripts.</summary>
internal static class ElevatedLaunch
{
    private const int UacDeclined = 1223;

    public static LaunchAction Plan(LaunchDecision decision, bool instanceRunning, ElevationState instanceState, bool relaunchElevated)
    {
        if (!instanceRunning)
            return decision.Elevated ? LaunchAction.StartElevated : LaunchAction.StartNormal;

        if (decision.Elevated && instanceState == ElevationState.NotElevated)
            return relaunchElevated ? LaunchAction.RelaunchElevated : LaunchAction.AskRelaunch;

        return LaunchAction.Reuse;
    }

    /// <summary>Starts the program with the UAC prompt. Throws <see cref="Win32Exception"/>; <see cref="IsDeclined"/> says the person said no.</summary>
    public static void StartElevated(string executable, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(' ', arguments.Select(Quote)),
        };

        // The handle is not kept: an elevated process cannot be inspected or ended from here, so it is not tracked either.
        Process.Start(info)?.Dispose();
    }

    public static bool IsDeclined(Win32Exception ex) => ex.NativeErrorCode == UacDeclined;

    private static string Quote(string argument) => argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"') ? argument : "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
