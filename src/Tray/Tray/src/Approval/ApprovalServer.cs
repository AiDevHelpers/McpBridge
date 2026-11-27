using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AiDevHelpers.Tray.Approval;

/// <summary>A dangerous tool call the HTTP MCP wants the user to approve. Same JSON shape as the HTTP MCP's record; a contract test runs its real client against this server.</summary>
public sealed record ApprovalRequest(string Nonce, string Tool, string ArgumentsHash, string Summary);

/// <summary>The user's answer, returning the nonce and hash it was given for.</summary>
public sealed record ApprovalResponse(string Nonce, string ArgumentsHash, bool Approved);

/// <summary>Shows the user one request and returns their answer. The tray shell implements it with a dialog.</summary>
public interface IApprovalPrompt
{
    Task<bool> AskAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>Another process already serves the approval pipe name, so no call can be confirmed.</summary>
public sealed class ApprovalPipeBusyException() : Exception(Strings.Get("ApprovalPipeBusy"));

/// <summary>
/// The tray's end of the approval pipe. It is the pipe server, created as the first instance for the current user only, so a
/// process that squats the name cannot receive requests. Before it reads anything it checks the client process
/// (<paramref name="isTrustedClient"/>); an untrusted client, a malformed request, a failing prompt or no answer in time
/// all mean "denied". One request is served at a time; others wait their turn and the client's own connect timeout
/// refuses them if it is too long.
/// </summary>
public sealed class ApprovalServer(string pipeName, Func<int, bool> isTrustedClient, IApprovalPrompt prompt, TimeSpan promptTimeout)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(5);
    private const string HttpExeName = "AiDevHelpers.MCP.Http.exe";

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The pipe for the current user.</summary>
    public static string DefaultPipeName => "AiDevHelpers.Tray.Approval." + WindowsIdentity.GetCurrent().User!.Value;

    /// <summary>Completes when the pipe exists and the server accepts connections; faults with <see cref="ApprovalPipeBusyException"/> when another process serves the name.</summary>
    public Task Ready => _ready.Task;

    /// <summary>True for the HTTP MCP exe in the folder this exe runs from (they ship in one package).</summary>
    public static bool IsHttpMcpInThisFolder(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var path = process.MainModule?.FileName;
            return path is not null
                && string.Equals(Path.GetFileName(path), HttpExeName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetDirectoryName(path)?.TrimEnd('\\'), AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        NamedPipeServerStream pipe;
        try
        {
            pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            var busy = new ApprovalPipeBusyException();
            _ready.TrySetException(busy);
            throw busy;
        }

        _ready.TrySetResult();
        await using (pipe.ConfigureAwait(false))
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await ServeAsync(pipe, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // A client that vanished mid-request is not the server's problem; wait for the next one.
                }
                finally
                {
                    if (pipe.IsConnected)
                    {
                        try
                        {
                            pipe.Disconnect();
                        }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException)
                        {
                        }
                    }
                }
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var clientPid) || !isTrustedClient((int)clientPid))
        {
            return;
        }

        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var readLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readLimit.CancelAfter(RequestReadTimeout);
        var line = await reader.ReadLineAsync(readLimit.Token).ConfigureAwait(false);
        var request = Parse(line);
        if (request is null)
        {
            return;
        }

        var approved = await AskAsync(request, cancellationToken).ConfigureAwait(false);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new ApprovalResponse(request.Nonce, request.ArgumentsHash, approved), Json).AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> AskAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(promptTimeout);
        try
        {
            return await prompt.AskAsync(request, limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static ApprovalRequest? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            var request = JsonSerializer.Deserialize<ApprovalRequest>(line, Json);
            return request is { Nonce.Length: > 0, ArgumentsHash.Length: > 0, Tool.Length: > 0 } ? request : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
}
