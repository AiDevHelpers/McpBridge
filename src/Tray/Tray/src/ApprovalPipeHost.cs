using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using AiDevHelpers.MCP.Core.Services;

namespace AiDevHelpers.Tray;

/// <summary>
/// The tray's end of the approval pipe. Only the signed-in user can connect, and the first instance owns the name, so another
/// process cannot take it over. Each connection carries one request; <paramref name="decide"/> answers it. A request that fails
/// gets no reply, and the MCP server refuses it.
/// </summary>
public static class ApprovalPipeHost
{
    public static async Task RunAsync(string pipeName, Func<ApprovalRequest, Task<bool>> decide, CancellationToken ct)
    {
        // The next instance is created before the current one serves, so a client never finds the name without a waiting instance.
        var pipe = Create(pipeName, firstInstance: true);
        NamedPipeServerStream? next = null;
        try
        {
            while (true)
            {
                next = Create(pipeName, firstInstance: false);
                try
                {
                    await ApprovalPipeServer.ServeOnceAsync(pipe, decide, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // No reply for this request; the caller refuses it.
                }
                finally
                {
                    pipe.Dispose();
                }

                pipe = next;
                next = null;
            }
        }
        finally
        {
            next?.Dispose();
        }
    }

    private static NamedPipeServerStream Create(string pipeName, bool firstInstance)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No user SID for the approval pipe.");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        // CurrentUserOnly cannot be combined with an explicit PipeSecurity, so the ACL above is what limits the pipe to this user.
        var options = PipeOptions.Asynchronous;
        if (firstInstance)
        {
            options |= PipeOptions.FirstPipeInstance;
        }

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }
}
