using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static AiDevHelpers.Helpers.WindowsSession.NativeMethods;

namespace AiDevHelpers.Helpers.WindowsSession;

/// <summary>What to start in a user's session, and where.</summary>
/// <param name="FileName">Full path of the executable.</param>
/// <param name="Arguments">Arguments, already quoted for the command line; may be empty.</param>
/// <param name="SessionId">The session to start it in, from <see cref="UserSession"/>.</param>
/// <param name="WorkingDirectory">Working directory, or null for the default.</param>
public sealed record UserProcessStartInfo(string FileName, string Arguments, uint SessionId, string? WorkingDirectory = null);

/// <summary>
/// Starts a process on a logged-on user's desktop. Needs SeTcbPrivilege (LocalSystem holds it); a LocalSystem
/// process lives in session 0, where nothing is visible, so the process is started with the user's token instead.
/// The user is already logged on in that session, so their profile (HKCU) is loaded and no LoadUserProfile call is made.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UserProcess
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const string DefaultDesktop = "winsta0\\default";

    /// <summary>
    /// Starts the process with the user's token and environment. Returns the process, or null when it already
    /// exited (a launcher such as wt.exe hands off to another process), so liveness is best effort.
    /// </summary>
    public static Process? Start(UserProcessStartInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.SessionId == UserSession.NoSession)
        {
            throw new InvalidOperationException(Strings.Get("NoConsoleUserSession"));
        }

        var commandLine = UserProcessCommandLine.Build(info.FileName, info.Arguments) + "\0";
        using var token = UserSession.OpenPrimaryToken(info.SessionId, bindToSession: true);
        IntPtr environment = IntPtr.Zero;
        try
        {
            if (!UserSession.TryCreateEnvironmentBlock(token, out environment))
            {
                environment = IntPtr.Zero;
            }

            var startup = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = DefaultDesktop,
            };

            if (!CreateProcessAsUserW(token.Handle, null, commandLine.ToCharArray(), IntPtr.Zero, IntPtr.Zero, false,
                    CreateUnicodeEnvironment, environment, info.WorkingDirectory, ref startup, out var created))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), Strings.Get("CreateProcessAsUserFailed"));
            }

            _ = CloseHandle(created.hThread);
            _ = CloseHandle(created.hProcess);
            try
            {
                return Process.GetProcessById((int)created.dwProcessId);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
        finally
        {
            UserSession.DestroyEnvironmentBlock(environment);
        }
    }
}
