using System.Diagnostics;
using System.Runtime.Versioning;
using AiDevHelpers.Helpers.WindowsSession;

namespace AiDevHelpers.Host;

/// <summary>Asks Windows where the logged-on users are.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSessionSource : ISessionSource
{
    public uint ConsoleSession => UserSession.ActiveConsoleSession();

    public bool HasUser(uint session) => UserSession.GetUserSid(session) is not null;

    public uint FirstLoggedOnSession() => UserSession.FindFirstLoggedOnSession();
}

/// <summary>A process started with the user's token, watched through its <see cref="Process"/>.</summary>
public sealed class ProcessChild(Process process) : IChildProcess
{
    public int Id => process.Id;

    public bool HasExited => process.HasExited;

    public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);

    public void Kill()
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    public void Dispose() => process.Dispose();
}

/// <summary>
/// Starts the server in the user's session with <see cref="UserProcess"/>. Before each start it prepares the data
/// folder for that session's user, so the server can create its token and endpoint record there.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UserProcessLauncher(string dataFolder) : ISessionLauncher
{
    public IChildProcess? Start(string fileName, string arguments, uint sessionId, string workingDirectory)
    {
        var user = UserSession.GetUserSid(sessionId) ?? throw new InvalidOperationException(Strings.Get("NoUserInSession"));
        McpDataFolder.Ensure(dataFolder, user);
        var process = UserProcess.Start(new UserProcessStartInfo(fileName, arguments, sessionId, workingDirectory));
        return process is null ? null : new ProcessChild(process);
    }
}
