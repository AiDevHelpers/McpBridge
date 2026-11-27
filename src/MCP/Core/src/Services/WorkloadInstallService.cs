using System.Collections.Concurrent;
using AiDevHelpers.Common;
using AiDevHelpers.MCP.Core.Tools;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>A .NET workload install waiting for the user's yes. Nothing runs until <see cref="WorkloadInstallService.ConfirmAsync"/> gets the token.</summary>
public sealed record WorkloadPlan(string Token, string Workload, string Display, string Command, DateTime ExpiresUtc);

/// <summary>The result of a plan or confirm. Error means nothing was planned or run; Message is what to tell the user.</summary>
public sealed record WorkloadOutcome(bool Ok, string? Message, string? Error, WorkloadPlan? Plan = null, int? ExitCode = null, string? Output = null);

/// <summary>
/// Plans and runs installs of the workloads in <see cref="InstallableWorkloads"/>. Plan returns the exact command and a single-use
/// token; the user's yes in chat is the permission, then confirm runs the install through the elevated script helper. The name is
/// checked against the table here and again by elev.exe and by the script; no id from a caller reaches dotnet.
/// </summary>
public sealed class WorkloadInstallService(IElevInstaller installer, Func<DateTime>? clock = null)
{
    private static readonly TimeSpan TokenLife = TimeSpan.FromMinutes(15);

    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly ConcurrentDictionary<string, WorkloadPlan> _pending = new();

    /// <param name="workload">A name from the installable table.</param>
    /// <param name="installed">Workload names already installed, from dotnet workload list.</param>
    public WorkloadOutcome Plan(string workload, IReadOnlyCollection<string> installed)
    {
        if (InstallableWorkloads.TryGet(workload) is not { } entry)
        {
            return new WorkloadOutcome(false, null, Strings.Format("WorkloadUnknown", workload, string.Join(", ", InstallableWorkloads.All.Select(w => w.Name))));
        }

        if (installed.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            return new WorkloadOutcome(true, Strings.Format("WorkloadAlreadyInstalled", entry.Name), null);
        }

        var plan = new WorkloadPlan(
            Guid.NewGuid().ToString("N"),
            entry.Name,
            entry.Display,
            $"dotnet workload install {entry.Name}",
            _now() + TokenLife);
        _pending[plan.Token] = plan;
        return new WorkloadOutcome(true, Strings.Format("WorkloadPlanned", entry.Display) + " " + Strings.Get("WorkloadNeedsAdmin"), null, plan);
    }

    /// <summary>Runs a planned install once, through the elevated script helper. An unknown, used or expired token is refused.</summary>
    public async Task<WorkloadOutcome> ConfirmAsync(string token, CancellationToken ct)
    {
        if (!_pending.TryRemove(token, out var plan) || _now() > plan.ExpiresUtc)
        {
            return new WorkloadOutcome(false, null, Strings.Get("WorkloadUnknownToken"));
        }

        var result = await installer.InstallWorkloadAsync(plan.Workload, ct);
        if (result.ExitCode == 0)
        {
            return new WorkloadOutcome(true, Strings.Format("WorkloadDone", plan.Workload), null, plan, 0, Tail(result.Output));
        }

        return new WorkloadOutcome(false, null, Strings.Format("WorkloadFailed", result.ExitCode, Tail(result.Output)), plan, result.ExitCode, Tail(result.Output));
    }

    private static string Tail(string text) => text.Length <= 2000 ? text : text[^2000..];
}
