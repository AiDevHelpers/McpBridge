namespace AiDevHelpers.Helpers.TerminalControl.Core;

using System.Diagnostics;
using System.Security.Principal;
using AiDevHelpers.Helpers.TerminalControl.Contracts;
using AiDevHelpers.Helpers.WindowsSession;

/// <summary>
/// Starts a terminal in the logged-on user's session. Needed when the host runs as LocalSystem: a LocalSystem
/// process lives in session 0, where it cannot show a window. Token and session calls come from AiDevHelpers.Helpers.WindowsSession.
/// </summary>
public sealed class ConsoleUserTerminalLauncher : ITerminalProcessLauncher
{
    public Process? Start(ProcessStartInfo startInfo) =>
        ConsoleUserSession.StartInUserSession(startInfo.FileName, startInfo.Arguments);
}

public static class ConsoleUserSession
{
    /// <summary>True when this process runs as LocalSystem.</summary>
    public static bool RunningAsLocalSystem() => UserSession.IsRunningAsLocalSystem();

    /// <summary>SID of the user logged on to the first active session, or null when none is logged on.</summary>
    public static SecurityIdentifier? GetConsoleUserSid()
    {
        uint sessionId = UserSession.FindFirstLoggedOnSession();
        return sessionId == UserSession.NoSession ? null : UserSession.GetUserSid(sessionId);
    }

    /// <summary>Starts <paramref name="fileName"/> with <paramref name="arguments"/> on the logged-on user's desktop.</summary>
    public static Process? StartInUserSession(string fileName, string arguments)
    {
        uint sessionId = UserSession.FindFirstLoggedOnSession();
        if (sessionId == UserSession.NoSession)
        {
            throw new InvalidOperationException(Strings.Get("NoActiveUserSession"));
        }

        return UserProcess.Start(new UserProcessStartInfo(fileName, arguments, sessionId));
    }

}
