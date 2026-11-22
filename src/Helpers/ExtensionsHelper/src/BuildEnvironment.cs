using System.Text.Json;

namespace AiDevHelpers.Helpers.ExtensionsHelper;

/// <summary>One Visual Studio install. <see cref="VsSdkTargetsPath"/> is null when the extension development workload is not installed.</summary>
public sealed record VsInstall(string Path, string DisplayName, string Version, bool IsPrerelease, string? MsBuildPath, string? VsSdkTargetsPath);

/// <summary>Finds the Visual Studio installs on this machine, their MSBuild, and whether each has the VSSDK targets a VSIX build needs.</summary>
public sealed class BuildEnvironment(ICommandRunner runner, string vswherePath)
{
    public async Task<IReadOnlyList<VsInstall>> ListAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(vswherePath, ["-all", "-prerelease", "-products", "*", "-format", "json"], ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(Strings.Format("VsWhereFailed", result.ExitCode, result.Stderr.Trim()));
        }

        if (string.IsNullOrWhiteSpace(result.Stdout))
        {
            return [];
        }

        var installs = new List<VsInstall>();
        using var doc = JsonDocument.Parse(result.Stdout);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var path = item.GetProperty("installationPath").GetString() ?? string.Empty;
            installs.Add(new VsInstall(
                path,
                item.TryGetProperty("displayName", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("installationVersion", out var version) ? version.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("isPrerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                FindMsBuild(path),
                FindVsSdkTargets(path)));
        }

        return installs;
    }

    private static string? FindMsBuild(string installPath)
    {
        var exe = Path.Combine(installPath, "MSBuild", "Current", "Bin", "MSBuild.exe");
        return File.Exists(exe) ? exe : null;
    }

    private static string? FindVsSdkTargets(string installPath)
    {
        var root = Path.Combine(installPath, "MSBuild", "Microsoft", "VisualStudio");
        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory.EnumerateDirectories(root, "v*")
            .Select(dir => Path.Combine(dir, "VSSDK", "Microsoft.VsSDK.targets"))
            .FirstOrDefault(File.Exists);
    }
}
