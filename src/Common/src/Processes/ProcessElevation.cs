using System.Runtime.InteropServices;

namespace AiDevHelpers.Common.Processes;

/// <summary>Whether a running process holds an elevated (administrator) token.</summary>
public enum ElevationState
{
    /// <summary>The process's token says it is not elevated.</summary>
    NotElevated,

    /// <summary>The process is elevated.</summary>
    Elevated,

    /// <summary>The process could not be inspected at all (it ended, or belongs to another account such as SYSTEM).</summary>
    Unknown,
}

/// <summary>
/// Reads the elevation of another process from its pid, without being elevated itself. Spike result (Windows 11, this repo): a
/// non-elevated caller can read the token of a same-user process that is not elevated, and cannot open processes of another account
/// (SYSTEM) at all. For a same-user process that is elevated, a live check on Windows 11 (an elevated Visual Studio read from a
/// non-elevated shell) showed the token query succeeds and reports TokenElevation = 1, so the normal path returns
/// <see cref="ElevationState.Elevated"/>. The access-denied branch in <see cref="Of"/> is not exercised by that check; it is a
/// fallback for a Windows build that refuses the token query on an openable process.
/// </summary>
public static class ProcessElevation
{
    private const uint QueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;
    private const int AccessDenied = 5;

    public static ElevationState Of(int pid)
    {
        if (!OperatingSystem.IsWindows())
            return ElevationState.Unknown;

        var process = OpenProcess(QueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
            return ElevationState.Unknown;

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
                return Marshal.GetLastWin32Error() == AccessDenied ? ElevationState.Elevated : ElevationState.Unknown;

            try
            {
                return GetTokenInformation(token, TokenElevationClass, out var elevated, sizeof(int), out _)
                    ? (elevated != 0 ? ElevationState.Elevated : ElevationState.NotElevated)
                    : ElevationState.Unknown;
            }
            finally
            {
                _ = CloseHandle(token);
            }
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    /// <summary>The current process, through the same path, so tests and callers compare like with like.</summary>
    public static ElevationState Current() => Of(Environment.ProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out int tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
