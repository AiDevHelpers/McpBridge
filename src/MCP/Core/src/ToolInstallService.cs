using System.Collections.Concurrent;
using AiDevHelpers.Common;
using AiDevHelpers.Common.Terminals;
using AiDevHelpers.MCP.Core.Tools;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>An install waiting for the user's yes. Nothing runs until <see cref="ToolInstallService.ConfirmAsync"/> gets the token.</summary>
public sealed record InstallPlan(string Token, string Tool, string Display, string PackageId, string Command, bool NeedsAdmin, DateTime ExpiresUtc);

/// <summary>The result of a plan, confirm or decline. Error means nothing was planned or run; Message is what to tell or ask the user.</summary>
public sealed record InstallOutcome(bool Ok, string? Message, string? Error, InstallPlan? Plan = null, int? ExitCode = null, string? Output = null);

/// <summary>
/// Plans and runs installs of the few tools in <see cref="InstallableTools"/>. Two steps, like a git write: the plan returns the exact
/// command and a single-use token, and the AI asks the user; the user's yes in chat is the permission, after which the AI confirms the
/// token. The install itself runs through the elevated script helper (the approved <c>install-tool.ps1</c>), so there is no second UAC
/// path, and the script re-checks the name against its own table. winget is the only installer, and no id or name from a repo is used.
/// </summary>
public sealed class ToolInstallService(IElevInstaller installer, ITerminalProbe probe, TerminalRegistry registry, Func<DateTime>? clock = null)
{
    private const string DeclinePrefix = "install:";
    private static readonly TimeSpan TokenLife = TimeSpan.FromMinutes(15);

    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly ConcurrentDictionary<string, InstallPlan> _pending = new();

    public InstallOutcome Plan(string tool)
    {
        if (string.Equals(tool, "wsl", StringComparison.OrdinalIgnoreCase))
            return new InstallOutcome(true, Strings.Get("InstallWslInstructions"), null);

        if (InstallableTools.TryGet(tool) is not { } entry)
        {
            return new InstallOutcome(false, null, Strings.Format("InstallUnknownTool", tool, string.Join(", ", InstallableTools.All.Select(t => t.Name))));
        }

        if (probe.FindOnPath("winget.exe") is null)
            return new InstallOutcome(false, null, Strings.Get("InstallNoWinget"));

        var plan = new InstallPlan(
            Guid.NewGuid().ToString("N"),
            entry.Name,
            entry.Display,
            entry.PackageId,
            $"winget install --id {entry.PackageId} --exact --source winget",
            entry.NeedsAdmin,
            _now() + TokenLife);
        _pending[plan.Token] = plan;

        var message = Strings.Format("InstallPlanned", entry.Display) + (entry.NeedsAdmin ? " " + Strings.Get("InstallNeedsAdmin") : string.Empty);
        return new InstallOutcome(true, message, null, plan);
    }

    /// <summary>Runs a planned install once, through the elevated script helper. An unknown, used or expired token is refused.</summary>
    public async Task<InstallOutcome> ConfirmAsync(string token, CancellationToken ct)
    {
        if (!_pending.TryRemove(token, out var plan) || _now() > plan.ExpiresUtc)
            return new InstallOutcome(false, null, Strings.Get("InstallUnknownToken"));

        var result = await installer.InstallAsync(plan.Tool, ct);
        if (result.ExitCode == 0)
            return new InstallOutcome(true, Strings.Format("InstallDone", plan.Display), null, plan, 0, Tail(result.Output));

        // elev.exe prints "refused: <reason>" when the broker would not run it, for example before "setup tools" was done.
        var refused = result.Output.Contains("refused:", StringComparison.Ordinal);
        var error = Strings.Format("InstallFailed", result.ExitCode, Tail(result.Output)) + (refused ? " " + Strings.Get("InstallHelperMissing") : string.Empty);
        return new InstallOutcome(false, null, error, plan, result.ExitCode, Tail(result.Output));
    }

    /// <summary>Remembers that the user does not want this tool, so the offer is not repeated.</summary>
    public InstallOutcome Decline(string tool)
    {
        if (InstallableTools.TryGet(tool) is not { } entry)
            return new InstallOutcome(false, null, Strings.Format("InstallUnknownTool", tool, string.Join(", ", InstallableTools.All.Select(t => t.Name))));

        registry.Decline(DeclinePrefix + entry.Name);
        return new InstallOutcome(true, Strings.Format("InstallDeclined", entry.Display), null);
    }

    /// <summary>The offer to put in a result when this interpreter is missing: null when it is not installable or the user declined it.</summary>
    public static string? OfferFor(string interpreterName, TerminalChoices choices)
    {
        if (InstallableTools.ForInterpreter(interpreterName) is not { } tool
            || choices.Declined.Contains(DeclinePrefix + tool.Name, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        return Strings.Format("InstallOffer", tool.Display, tool.Name);
    }

    private static string Tail(string text) => text.Length <= 2000 ? text : text[^2000..];
}
