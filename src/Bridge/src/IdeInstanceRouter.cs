using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AiDevHelpers.Bridge;

/// <summary>
/// Routes a common IDE action to the right running IDE instance and reports which instance answered.
/// Visual Studio instances are reached over their per-process named pipe (AiDevHelpers.VS.&lt;pid&gt;).
/// </summary>
public static class IdeInstanceRouter
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs <paramref name="action"/> on the instance whose solution matches <paramref name="solutionPath"/>.
    /// With no solution given, the single running instance is used; several instances is an error.
    /// Returns a JSON object that always includes <c>ide</c>, <c>instance_pid</c>, and <c>solution_path</c>.
    /// </summary>
    public static async Task<string> CallAsync(string action, object args, string? solutionPath, CancellationToken ct, string? ide = null, int? instancePid = null)
    {
        var instances = VsInstanceRegistry.ListRunning();
        if (!string.IsNullOrWhiteSpace(ide))
        {
            // Only instances of the requested IDE ("VisualStudio" or "VsCode") are candidates.
            instances = instances.Where(i => string.Equals(i.Ide, ide, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (instancePid is int pid)
        {
            instances = instances.Where(i => i.Pid == pid).ToList();
            if (instances.Count == 0)
            {
                return Error($"No running IDE instance has process id {pid}.", VsInstanceRegistry.ListRunning());
            }
        }

        VsInstance? target;
        if (!string.IsNullOrWhiteSpace(solutionPath))
        {
            var matches = instances.Where(i => SamePath(i.SolutionPath, solutionPath)).ToList();
            if (matches.Count == 0)
            {
                return Error($"No open Visual Studio instance has '{solutionPath}' open.", instances);
            }

            if (matches.Count > 1 && instancePid is null)
            {
                // The stored preference only breaks this tie. An explicit ide or instancePid has already decided.
                var preferred = string.IsNullOrWhiteSpace(ide) ? RepoConfig.PreferredIde(solutionPath) : null;
                var narrowed = preferred is null
                    ? matches
                    : matches.Where(i => string.Equals(i.Ide, preferred, StringComparison.OrdinalIgnoreCase)).ToList();
                if (narrowed.Count != 1)
                {
                    return Error("Several instances have this solution open; pass instancePid to choose one.", matches);
                }

                matches = narrowed;
            }

            target = matches[0];
        }
        else if (instances.Count == 0)
        {
            return Error(string.IsNullOrWhiteSpace(ide) ? "No Visual Studio instance is running with a solution open." : $"No {ide} instance is running.", instances);
        }
        else if (instances.Count > 1)
        {
            return Error("Several Visual Studio instances are open; pass solutionPath to choose one.", instances);
        }
        else
        {
            target = instances[0];
        }

        return await SendAsync(target, action, args, ct);
    }

    /// <summary>JSON for <c>IdeListInstances</c>: every running instance with its solution.</summary>
    public static string ListInstances()
    {
        var instances = VsInstanceRegistry.ListRunning();
        return JsonSerializer.Serialize(new
        {
            count = instances.Count,
            instances = instances.Select(Describe),
            // Launched but the bridge has not answered yet: listed so a caller can see it is still starting.
            starting = StartingInstances.List(),
        });
    }

    // One request line out, one reply line back. Both steps can block on a silent server, so the caller races this.
    private static async Task<string?> ExchangeAsync(NamedPipeClientStream pipe, string request)
    {
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        await writer.WriteLineAsync(request);
        return await reader.ReadLineAsync();
    }

    private static async Task<string> SendAsync(
        VsInstance target,
        string action,
        object args,
        CancellationToken ct)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", target.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            // ConnectAsync does not reliably honor its token, so race it against the timeout.
            var connecting = pipe.ConnectAsync();
            var connected = await Task.WhenAny(connecting, Task.Delay(ConnectTimeout, ct));
            if (connected != connecting)
            {
                throw new TimeoutException("The IDE instance did not accept the connection in time.");
            }

            await connecting;

            // The write can block on a pipe whose server never reads, so the write and the read share one timeout.
            // On timeout the pipe is disposed, which aborts a stuck write.
            var exchange = ExchangeAsync(pipe, JsonSerializer.Serialize(new { action, args }));
            var finished = await Task.WhenAny(exchange, Task.Delay(ReplyTimeout, ct));
            if (finished != exchange)
            {
                pipe.Dispose();
                throw new TimeoutException("No reply from the IDE within the timeout.");
            }
            var line = await exchange;

            using var reply = JsonDocument.Parse(line ?? "{}");
            var ok = reply.RootElement.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();

            return JsonSerializer.Serialize(new
            {
                ide = target.Ide,
                instance_pid = target.Pid,
                solution_path = target.SolutionPath,
                ok,
                result = ok && reply.RootElement.TryGetProperty("result", out var r) ? r.Clone() : (JsonElement?)null,
                error = !ok && reply.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null,
            });
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or JsonException or OperationCanceledException or UnauthorizedAccessException or ObjectDisposedException)
        {
            var reason = ex is UnauthorizedAccessException
                ? "the instance's pipe refused this client (often a process running at a different elevation)"
                : ex.Message;
            // A timeout means the process is still there but not answering (busy or hung), not that it is gone.
            var busy = ex is TimeoutException;
            return JsonSerializer.Serialize(new
            {
                ide = target.Ide,
                instance_pid = target.Pid,
                solution_path = target.SolutionPath,
                ok = false,
                busy,
                error = $"Could not reach Visual Studio instance {target.Pid}: {reason}",
                hint = busy
                    ? "The instance is running but not answering. Check ide_bridge_health; if it stays silent, relaunch it with ide_launch_instance (relaunchElevated when the solution needs it) after the user agrees."
                    : null,
            });
        }
    }

    private static string Error(string message, IReadOnlyList<VsInstance> instances) =>
        JsonSerializer.Serialize(new
        {
            ok = false,
            error = message,
            instances = instances.Select(Describe),
        });

    private static object Describe(VsInstance i) => new
    {
        ide = i.Ide,
        pid = i.Pid,
        solution_path = i.SolutionPath,
        updated_utc = i.UpdatedUtc,
    };

    private static bool SamePath(string? a, string b) =>
        !string.IsNullOrEmpty(a) && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
