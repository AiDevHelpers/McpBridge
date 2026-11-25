using System.IO.Pipes;
using System.Text.Json;
using AiDevHelpers.Bridge;
using AiDevHelpers.Common;
using AiDevHelpers.Common.Processes;
using AiDevHelpers.MCP.Core.Services;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>How a solution gets a Visual Studio instance: through the broker when the repo allows it, otherwise through devenv.</summary>
internal interface ISolutionLauncher
{
    /// <summary>Starts devenv with the solution (normal path). Returns an error, or null when the launch started.</summary>
    string? LaunchDevenv(string solutionPath, out int? pid);

    /// <summary>Launches through the broker when the repo's allow-all and elevation policy allow it. Used is false when it did not apply.</summary>
    Task<(bool Used, string? Error)> TryBrokerLaunchAsync(string solutionPath, CancellationToken ct);
}

/// <summary>The real launcher: devenv, or the broker's run channel for a repo whose allow-all is approved.</summary>
internal sealed class DevenvSolutionLauncher(IElevRunner? elevRunner) : ISolutionLauncher
{
    // Repo-relative script that starts devenv. It runs through the broker, so it is covered by the repo's allow-all approval.
    private const string OpenScript = ".aidevhelpers/open-solution.ps1";

    public string? LaunchDevenv(string solutionPath, out int? pid) => DevenvLauncher.Open(solutionPath, out pid);

    public async Task<(bool Used, string? Error)> TryBrokerLaunchAsync(string solutionPath, CancellationToken ct)
    {
        if (elevRunner is null)
        {
            return (false, null);
        }

        var full = Path.GetFullPath(solutionPath);
        var repo = AiDevHelpers.Common.RepoSettingsFile.FindRepoRootFromFolder(Path.GetDirectoryName(full));
        if (repo is null || !AiDevHelpers.Common.ElevationPolicy.Decide(full).Elevated || !AllowAllApproved(repo))
        {
            return (false, null);
        }

        var result = await elevRunner.RunAsync(repo, OpenScript, [full], ct);
        return result.ExitCode == 0
            ? (true, null)
            : (true, Strings.Format("ElevatedOpenFailed", result.ExitCode, result.Output));
    }

    // True when this repo's settings file still has the hash the person approved allow-all for.
    private static bool AllowAllApproved(string repo)
    {
        var user = AiDevHelpers.Common.RepoSettingsFile.UserFilePath(repo);
        if (!File.Exists(user))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(user));
            if (!doc.RootElement.TryGetProperty("allowAll", out var entry))
            {
                return false;
            }

            var recorded = entry.GetProperty("settingsHash").GetString();
            var settings = AiDevHelpers.Common.RepoSettingsFile.SettingsPath(repo);
            return File.Exists(settings)
                && string.Equals(recorded, AiDevHelpers.Common.RepoSettingsFile.Hash(settings), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>
/// Opens a solution in Visual Studio and returns once its bridge answers. Reuses a running instance, waits with an injected delay so
/// the wait can be tested, and reports a timeout as "not ready" rather than failing.
/// </summary>
internal sealed class IdeSolutionOpener(
    ISolutionLauncher launcher,
    Func<string, VsInstance?> findInstance,
    Func<VsInstance, bool> bridgeAnswers,
    Func<TimeSpan, CancellationToken, Task> delay,
    Func<DateTime> now,
    TimeSpan readyTimeout)
{
    public async Task<string> OpenAsync(string solutionPath, CancellationToken ct)
    {
        if (!File.Exists(solutionPath))
        {
            return Json(new { ok = false, error = Strings.Get("SolutionFileMissing") });
        }

        // Reuse an instance that already has this solution open, so a second devenv is not started for it.
        var existing = findInstance(solutionPath);
        if (existing is not null)
        {
            return Json(new { ok = true, opened = solutionPath, already_running = true, instance_pid = existing.Pid });
        }

        // With an approved allow-all for the repo and an elevation policy, the launch goes through the broker's run channel,
        // so no UAC prompt. Otherwise the normal launch (with its own UAC prompt when elevation is needed) is used.
        var brokered = await launcher.TryBrokerLaunchAsync(solutionPath, ct);
        if (brokered.Error is not null)
        {
            return Json(new { ok = false, error = brokered.Error });
        }

        int? launchedPid = null;
        if (!brokered.Used)
        {
            var error = launcher.LaunchDevenv(solutionPath, out var startedPid);
            if (error is null && startedPid is { } launched)
            {
                StartingInstances.Write(launched, solutionPath);
                launchedPid = launched;
            }

            if (error is not null)
            {
                return Json(new { ok = false, error });
            }
        }

        // Return only once the new instance has registered and its bridge answers, so a follow-up call does not race it.
        var deadline = now() + readyTimeout;
        while (now() < deadline)
        {
            var started = findInstance(solutionPath);
            if (started is not null && bridgeAnswers(started))
            {
                StartingInstances.Clear(started.Pid);
                return Json(new { ok = true, opened = solutionPath, already_running = false, instance_pid = started.Pid, bridge_ready = true });
            }

            await delay(TimeSpan.FromMilliseconds(500), ct);
        }

        // A launch whose bridge never answered is not reported as starting: its marker is cleared.
        if (launchedPid is { } timedOut)
        {
            StartingInstances.Clear(timedOut);
        }

        return Json(new { ok = false, opened = solutionPath, bridge_ready = false, error = Strings.Get("OpenBridgeNotReady") });
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    /// <summary>True when the instance's pipe accepts a connection, which is how the bridge says it is ready.</summary>
    public static bool PipeAnswers(VsInstance instance)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", instance.PipeName, PipeDirection.InOut);
            pipe.Connect(1000);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
