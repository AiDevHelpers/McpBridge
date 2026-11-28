using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AiDevHelpers.Updates;

/// <summary>What one install attempt did.</summary>
public enum StagedInstallOutcome
{
    /// <summary>Nothing staged to install.</summary>
    None,

    /// <summary>The package was installed and is no longer staged.</summary>
    Applied,

    /// <summary>The package was set aside: it failed verification or was already installed.</summary>
    Rejected,

    /// <summary>The apply failed and was rolled back; the package was set aside.</summary>
    Failed,

    /// <summary>MCP.Http did not stop in time. The package stays staged for a later attempt.</summary>
    DrainFailed,
}

/// <summary>Result of one install attempt.</summary>
public sealed record StagedInstallResult(StagedInstallOutcome Outcome, string Message, string? Version);

/// <summary>
/// Installs the newest staged package (unit 10b). One package per call: verify, check the replay record, drain MCP.Http, apply with an
/// empty keep list (the install folder holds program files only; config and data live in ProgramData), record the install, and release MCP.Http.
/// A package that fails verification, is a replay, or fails to apply is renamed with <see cref="RejectedSuffix"/> so it is not retried
/// in a loop. A drain that times out leaves the package staged. The status of each attempt is written to status.json.
/// The Host calls this once before its supervisor starts (the startup path) and again from the watcher.
/// </summary>
public sealed class StagedUpdateInstaller(
    IUpdatePackageVerifier verifier,
    IUpdateApplier applier,
    IUpdateDrain drain,
    IMcpProcessControl control,
    UpdateReplayRecord replay,
    string updatesRoot,
    string installDir,
    ILogger<StagedUpdateInstaller> logger)
{
    /// <summary>Suffix for a package that was set aside.</summary>
    public const string RejectedSuffix = ".rejected";

    /// <summary>Backup of the install folder, kept during an apply. Dot-prefixed, so it is never read as a package.</summary>
    public const string BackupFolderName = ".backup";

    /// <summary>Status of the last attempt, written under the updates root.</summary>
    public const string StatusFileName = "status.json";

    /// <summary>Installs at most one staged package. <paramref name="allowDevPackages"/> admits unsigned or same-version dev packages (debug builds only).</summary>
    public async Task<StagedInstallResult> InstallNewestAsync(string currentVersion, bool allowDevPackages, CancellationToken ct)
    {
        string? candidate = FindNewestStaged(updatesRoot);
        if (candidate is null)
        {
            return await RecordAsync(new StagedInstallResult(StagedInstallOutcome.None, "no staged update", null), ct);
        }

        UpdatePackageVerification verification = await verifier.VerifyAsync(candidate, currentVersion, allowDevPackages, ct);
        if (!verification.IsValid || verification.Manifest is null)
        {
            Reject(candidate);
            return await RecordAsync(new StagedInstallResult(StagedInstallOutcome.Rejected, verification.Message, verification.Manifest?.Version), ct);
        }

        UpdateManifest manifest = verification.Manifest;
        if (await replay.IsReplayAsync(candidate, ct))
        {
            Reject(candidate);
            return await RecordAsync(new StagedInstallResult(StagedInstallOutcome.Rejected, "this package was already installed", manifest.Version), ct);
        }

        UpdateApplyResult apply;
        try
        {
            if (!await drain.DrainAsync(ct))
            {
                return await RecordAsync(new StagedInstallResult(StagedInstallOutcome.DrainFailed, "MCP.Http did not stop; the update stays staged", manifest.Version), ct);
            }

            apply = await applier.ApplyAsync(candidate, manifest, installDir, Path.Combine(updatesRoot, BackupFolderName), [], ct);
        }
        finally
        {
            control.Release();
        }

        if (!apply.Success)
        {
            Reject(candidate);
            return await RecordAsync(new StagedInstallResult(StagedInstallOutcome.Failed, apply.Message, manifest.Version), ct);
        }

        await replay.RecordAsync(candidate, ct);
        Directory.Delete(candidate, recursive: true);
        return await RecordAsync(new StagedInstallResult(StagedInstallOutcome.Applied, apply.Message, manifest.Version), ct);
    }

    /// <summary>
    /// The newest package folder that is ready to try: its name does not start with '.' (download, staging and backup folders),
    /// it does not end with <see cref="RejectedSuffix"/>, and it has a manifest. Versions are compared as numbers.
    /// </summary>
    public static string? FindNewestStaged(string updatesRoot)
    {
        if (!Directory.Exists(updatesRoot))
        {
            return null;
        }

        return Directory.EnumerateDirectories(updatesRoot)
            .Where(d =>
            {
                string name = Path.GetFileName(d);
                return !name.StartsWith('.')
                    && !name.EndsWith(RejectedSuffix, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(d, UpdateManifest.FileName));
            })
            .OrderByDescending(d => ParseVersion(Path.GetFileName(d)))
            .FirstOrDefault();
    }

    private static Version ParseVersion(string name)
    {
        string trimmed = name.TrimStart('v');
        int cut = trimmed.IndexOfAny(['-', '+']);
        return Version.TryParse(cut < 0 ? trimmed : trimmed[..cut], out Version? v) ? v : new Version(0, 0);
    }

    // Renames the package to <name>.rejected, replacing an older rejected copy. A failure to rename is logged and leaves the package as it was.
    private void Reject(string packageDir)
    {
        string target = packageDir + RejectedSuffix;
        try
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(packageDir, target);
        }
        catch (IOException ex)
        {
            logger.LogError("Could not set aside the rejected update: {Error} ({ExceptionType})", ex.Message, ex.GetType().Name);
        }
    }

    private async Task<StagedInstallResult> RecordAsync(StagedInstallResult result, CancellationToken ct)
    {
        if (result.Outcome is StagedInstallOutcome.Applied or StagedInstallOutcome.None)
        {
            logger.LogInformation("Staged update {Outcome}: {Message}", result.Outcome, result.Message);
        }
        else
        {
            logger.LogWarning("Staged update {Outcome}: {Message}", result.Outcome, result.Message);
        }

        try
        {
            Directory.CreateDirectory(updatesRoot);
            var status = new { result.Outcome, result.Message, result.Version, AtUtc = DateTimeOffset.UtcNow };
            await File.WriteAllTextAsync(Path.Combine(updatesRoot, StatusFileName), JsonSerializer.Serialize(status), ct);
        }
        catch (IOException ex)
        {
            logger.LogError("Could not write the update status file: {Error} ({ExceptionType})", ex.Message, ex.GetType().Name);
        }

        return result;
    }
}
