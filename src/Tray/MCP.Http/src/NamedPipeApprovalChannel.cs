using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AiDevHelpers.MCP.Http;

/// <summary>The process on the other end of the approval pipe is not the tray, so nothing was sent.</summary>
public sealed class UntrustedApprovalServerException() : Exception(Strings.Get("ApprovalServerUntrusted"));

/// <summary>
/// Asks the tray for approval over a user-session named pipe where the tray is the server. One line of JSON each way.
/// Before sending anything it checks the server process: <paramref name="isTrustedServer"/> gets the pid of the pipe's
/// server, so a process that squats the pipe name before the tray starts gets nothing and cannot approve a call.
/// </summary>
public sealed class NamedPipeApprovalChannel(string pipeName, Func<int, bool> isTrustedServer, TimeSpan connectTimeout) : IApprovalChannel
{
    private const string TrayExeName = "AiDevHelpers.Tray.exe";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The pipe the tray listens on for the current user.</summary>
    public static string DefaultPipeName => "AiDevHelpers.Tray.Approval." + WindowsIdentity.GetCurrent().User!.Value;

    /// <summary>
    /// Trusts a process that is the tray exe in the folder this exe runs from (they ship in one package).
    /// A different folder, such as a development build, needs a different check.
    /// </summary>
    public static bool IsTrayInThisFolder(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var path = process.MainModule?.FileName;
            return path is not null
                && string.Equals(Path.GetFileName(path), TrayExeName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetDirectoryName(path)?.TrimEnd('\\'), AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public async Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync((int)connectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);

        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var serverPid) || !isTrustedServer((int)serverPid))
        {
            throw new UntrustedApprovalServerException();
        }

        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, Json).AsMemory(), cancellationToken).ConfigureAwait(false);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException(Strings.Get("ApprovalPipeClosed"));
        return JsonSerializer.Deserialize<ApprovalResponse>(line, Json)
            ?? throw new IOException(Strings.Get("ApprovalPipeClosed"));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);
}
