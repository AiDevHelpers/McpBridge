using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiDevHelpers.Common.Processes;

namespace AiDevHelpers.Helpers.PackageHelper;

/// <summary>Runs dotnet. Injected so tests can see the exact arguments and supply the JSON.</summary>
public interface IPackageRunner
{
    Task<(int ExitCode, string Output)> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default);
}

/// <summary>Runs dotnet.exe through the shared Common runner and returns stdout and stderr together.</summary>
public sealed class ProcessPackageRunner : IPackageRunner
{
    public async Task<(int ExitCode, string Output)> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        var result = await CommandRunner.RunAsync(
            new CommandRequest { FileName = "dotnet", Arguments = arguments, WorkingDirectory = workingDirectory }, ct);
        if (!result.Launched)
        {
            throw new InvalidOperationException(Strings.Get("DotnetNotFound"));
        }

        return (result.ExitCode, result.StdOut + result.StdErr);
    }
}

/// <summary>
/// An update for one package in one project. <see cref="Chosen"/> is the version to move to, or null when nothing newer is allowed.
/// <see cref="Note"/> says why when it differs from <see cref="Latest"/>.
/// </summary>
public sealed record PackageUpdate(string Project, string Package, string Current, string Latest, string? Chosen, string Note);

/// <summary>
/// The update rule: a package that versions with .NET (System.*, Microsoft.Extensions.*, and the like) must not move past the .NET
/// major of the project, so a net10.0 project never takes a 11.x package. Other packages take the latest version.
/// </summary>
public static partial class PackageUpdateRule
{
    private static readonly string[] DotNetPrefixes =
        ["System.", "Microsoft.Extensions.", "Microsoft.AspNetCore.", "Microsoft.EntityFrameworkCore", "Microsoft.Bcl."];

    /// <summary>The highest .NET major among the frameworks (net5.0 or later); null when there is none, such as net48 or netstandard.</summary>
    public static int? MaxMajor(IEnumerable<string> frameworks)
    {
        int? max = null;
        foreach (var framework in frameworks)
        {
            var m = DotNetFramework().Match(framework);
            if (m.Success && int.Parse(m.Groups["major"].Value, CultureInfo.InvariantCulture) is var major && major >= 5 && (max is null || major > max))
            {
                max = major;
            }
        }

        return max;
    }

    /// <summary>The newest stable version whose major is at most <paramref name="maxMajor"/>, or null.</summary>
    public static string? NewestAllowed(IEnumerable<string> versions, int maxMajor) =>
        versions
            .Where(v => !v.Contains('-'))
            .Select(v => (Text: v, Parsed: Parse(v)))
            .Where(v => v.Parsed is not null && v.Parsed.Major <= maxMajor)
            .OrderByDescending(v => v.Parsed)
            .Select(v => v.Text)
            .FirstOrDefault();

    public static bool VersionsWithDotNet(string package) =>
        DotNetPrefixes.Any(prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    internal static Version? Parse(string version)
    {
        var core = version.Split('-', '+')[0];
        return Version.TryParse(core, out var parsed) ? parsed : null;
    }

    [GeneratedRegex(@"^net(?<major>\d+)\.\d+")]
    private static partial Regex DotNetFramework();
}

/// <summary>Lists the outdated packages of a solution with <c>dotnet list package --outdated</c> and applies <see cref="PackageUpdateRule"/>. Edits nothing.</summary>
public sealed partial class OutdatedPlanner(IPackageRunner runner)
{
    public async Task<IReadOnlyList<PackageUpdate>> PlanAsync(string workingDirectory, string solution, CancellationToken ct = default)
    {
        var (exit, output) = await runner.RunAsync(workingDirectory, ["list", solution, "package", "--outdated", "--format", "json"], ct);
        if (exit != 0)
        {
            throw new InvalidOperationException(Strings.Format("PackageListFailed", exit, output.Trim()));
        }

        var updates = new List<PackageUpdate>();
        using var doc = JsonDocument.Parse(JsonFrom(output));
        if (!doc.RootElement.TryGetProperty("projects", out var projects))
        {
            return updates;
        }

        foreach (var project in projects.EnumerateArray())
        {
            var path = project.GetProperty("path").GetString() ?? string.Empty;
            var frameworks = project.TryGetProperty("frameworks", out var f) ? f.EnumerateArray().ToList() : [];
            var cap = PackageUpdateRule.MaxMajor(frameworks.Select(x => x.GetProperty("framework").GetString() ?? string.Empty));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var framework in frameworks)
            {
                if (!framework.TryGetProperty("topLevelPackages", out var packages))
                {
                    continue;
                }

                foreach (var package in packages.EnumerateArray())
                {
                    var id = package.GetProperty("id").GetString() ?? string.Empty;
                    var current = package.GetProperty("resolvedVersion").GetString() ?? string.Empty;
                    var latest = package.GetProperty("latestVersion").GetString() ?? string.Empty;
                    if (seen.Add($"{id}|{current}"))
                    {
                        updates.Add(await PlanOneAsync(workingDirectory, path, id, current, latest, cap, ct));
                    }
                }
            }
        }

        return updates;
    }

    private async Task<PackageUpdate> PlanOneAsync(string workingDirectory, string project, string id, string current, string latest, int? cap, CancellationToken ct)
    {
        var latestMajor = PackageUpdateRule.Parse(latest)?.Major;
        if (cap is not int max || !PackageUpdateRule.VersionsWithDotNet(id) || latestMajor is not int major || major <= max)
        {
            return new PackageUpdate(project, id, current, latest, latest, string.Empty);
        }

        var (_, output) = await runner.RunAsync(workingDirectory, ["package", "search", id, "--exact-match", "--format", "json"], ct);
        var chosen = PackageUpdateRule.NewestAllowed(Versions(output), max);
        var newer = chosen is not null && PackageUpdateRule.Parse(chosen) is { } picked
            && (PackageUpdateRule.Parse(current) is not { } now || picked > now);
        return newer
            ? new PackageUpdate(project, id, current, latest, chosen, Strings.Format("UpdateCapped", latest, major, max, chosen!))
            : new PackageUpdate(project, id, current, latest, null, Strings.Format("UpdateNothingAllowed", latest, major, max));
    }

    private static IEnumerable<string> Versions(string output)
    {
        using var doc = JsonDocument.Parse(JsonFrom(output));
        if (!doc.RootElement.TryGetProperty("searchResult", out var sources))
        {
            return [];
        }

        return sources.EnumerateArray()
            .Where(s => s.TryGetProperty("packages", out _))
            .SelectMany(s => s.GetProperty("packages").EnumerateArray())
            .Select(p => p.GetProperty("version").GetString() ?? string.Empty)
            .Where(v => v.Length > 0)
            .Distinct()
            .ToList();
    }

    // dotnet can print warnings before the JSON, and the runner merges stderr into the output.
    private static string JsonFrom(string output)
    {
        var start = OpeningBrace().Match(output);
        return start.Success ? output[start.Index..] : output;
    }

    [GeneratedRegex(@"(?m)^\{\s*$")]
    private static partial Regex OpeningBrace();
}
