using AiDevHelpers.Bridge;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// Launches and stops Visual Studio instances on request. Launch reuses an instance that already has the solution
/// open. Kill first asks the instance to save every document, and refuses if any document stays unsaved.
/// </summary>
internal static class IdeInstanceLifecycle
{
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The devenv arguments that open <paramref name="solutionPath"/> in a new instance.</summary>
    internal static IReadOnlyList<string> LaunchArguments(string solutionPath) => [solutionPath];

    public static async Task<string> LaunchAsync(string solutionPath, CancellationToken ct)
    {
        if (!File.Exists(solutionPath))
        {
            return Error(Strings.Format("SolutionFileNotFound", solutionPath));
        }

        var existing = Find(solutionPath);
        if (existing is not null)
        {
            return Ok(new { already_running = true, instance_pid = existing.Pid, solution_path = existing.SolutionPath });
        }

        var devenv = IDELauncherService.FindVisualStudioPath();
        if (devenv is null)
        {
            return Error(Strings.Get("DevenvNotFoundByVswhere"));
        }

        var startInfo = new ProcessStartInfo(devenv) { UseShellExecute = false };
        // No /edit: that switch opens the file in a running instance, so a second solution never gets its own.
        foreach (var argument in LaunchArguments(solutionPath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using (var process = Process.Start(startInfo))
        {
            // devenv can hand the solution to an already-running instance and exit, so the launched pid is not
            // the instance pid. The instance is found through its bridge manifest below.
            ManagedProcessRegistry.Track(process, "IDE launch");
        }

        var deadline = DateTime.UtcNow + LaunchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var found = Find(solutionPath);
            if (found is not null)
            {
                ManagedProcessRegistry.TrackPid(found.Pid, "IDE launch");
                return Ok(new { already_running = false, instance_pid = found.Pid, solution_path = found.SolutionPath });
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return Error(Strings.Format("BridgeNotRegistered", solutionPath, LaunchTimeout.TotalSeconds));
    }

    /// <summary>
    /// Saves all documents in the target instance, then kills it. Refuses, and kills nothing, if any document is
    /// still unsaved. The process must be devenv, so the kill can't reach an unrelated process.
    /// </summary>
    public static async Task<string> KillAsync(string? solutionPath, CancellationToken ct)
    {
        var reply = await IdeInstanceRouter.CallAsync("prepareKill", new { }, solutionPath, ct);
        using var doc = JsonDocument.Parse(reply);
        var root = doc.RootElement;

        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            return reply;
        }

        var result = root.GetProperty("result");
        if (!result.GetProperty("safe").GetBoolean())
        {
            return Error(Strings.Get("NotKilledUnsaved"), result.GetProperty("unsaved").Clone());
        }

        var pid = root.GetProperty("instance_pid").GetInt32();
        using (var process = Process.GetProcessById(pid))
        {
            if (!string.Equals(process.ProcessName, "devenv", StringComparison.OrdinalIgnoreCase))
            {
                return Error(Strings.Format("NotKilledWrongProcess", pid, process.ProcessName));
            }

            process.Kill(entireProcessTree: true);
        }

        return Ok(new { killed = true, instance_pid = pid });
    }

    private static VsInstance? Find(string solutionPath) =>
        VsInstanceRegistry.ListRunning().FirstOrDefault(i =>
            !string.IsNullOrEmpty(i.SolutionPath) &&
            string.Equals(Path.GetFullPath(i.SolutionPath), Path.GetFullPath(solutionPath), StringComparison.OrdinalIgnoreCase));

    private static string Ok(object payload) => JsonSerializer.Serialize(new { ok = true, ide = "VisualStudio", result = payload });

    private static string Error(string message, object? unsaved = null) =>
        JsonSerializer.Serialize(new { ok = false, ide = "VisualStudio", error = message, unsaved });
}
