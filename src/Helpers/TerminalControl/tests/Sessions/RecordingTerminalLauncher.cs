namespace AiDevHelpers.Helpers.TerminalControl.Tests;

using System.Diagnostics;
using AiDevHelpers.Helpers.TerminalControl.Contracts;

/// <summary>Records launch requests instead of starting real Windows Terminal processes.</summary>
internal sealed class RecordingTerminalLauncher : ITerminalProcessLauncher
{
    public List<ProcessStartInfo> Started { get; } = new();
    public Exception? ThrowOnStart { get; set; }

    public Process? Start(ProcessStartInfo startInfo)
    {
        if (ThrowOnStart is not null)
        {
            throw ThrowOnStart;
        }

        Started.Add(startInfo);
        return null;
    }
}
