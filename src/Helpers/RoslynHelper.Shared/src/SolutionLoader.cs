using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace AiDevHelpers.Helpers.RoslynHelper;

/// <summary>One project's load result: <c>project</c> is its path relative to the solution folder.</summary>
public sealed record ProjectLoadResult(
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error);

/// <summary>A loaded solution and one load result per project in the solution file.</summary>
public sealed class LoadedSolution
{
    public LoadedSolution(Solution solution, IReadOnlyList<ProjectLoadResult> projects)
    {
        Solution = solution;
        Projects = projects;
    }

    public Solution Solution { get; }

    public IReadOnlyList<ProjectLoadResult> Projects { get; }
}

/// <summary>
/// Opens a solution with MSBuildWorkspace and caches the result per full path. A project that fails to load does not
/// fail the load: it gets an entry with <c>Ok = false</c> and the MSBuild diagnostic text.
/// </summary>
public sealed class SolutionLoader
{
    private static readonly object RegisterGate = new();

    private static readonly Regex SlnProjectLine = new(
        @"^Project\(""\{[^}]*\}""\)\s*=\s*""[^""]*"",\s*""([^""]+)"",\s*""\{",
        RegexOptions.Multiline);

    private readonly object _gate = new();
    private readonly Dictionary<string, Task<LoadedSolution>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<LoadedSolution> LoadAsync(string solutionPath)
    {
        var key = Path.GetFullPath(solutionPath);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var task = LoadCoreAsync(key);
            _cache[key] = task;
            task.ContinueWith(
                _ =>
                {
                    lock (_gate)
                    {
                        _cache.Remove(key);
                    }
                },
                TaskContinuationOptions.OnlyOnFaulted);
            return task;
        }
    }

    private static async Task<LoadedSolution> LoadCoreAsync(string solutionPath)
    {
        if (!File.Exists(solutionPath))
        {
            throw new InvalidOperationException(Strings.Format("SolutionNotFound", solutionPath));
        }

        var listed = ReadProjectPaths(solutionPath);
        EnsureMSBuildRegistered();
        var (solution, diagnostics) = await OpenAsync(solutionPath).ConfigureAwait(false);
        return new LoadedSolution(solution, BuildResults(solutionPath, listed, solution, diagnostics));
    }

    /// <summary>
    /// Registers the .NET 10 SDK's MSBuild. It must run before any Microsoft.Build type is used, so it is kept
    /// out of <see cref="OpenAsync"/>.
    /// </summary>
    private static void EnsureMSBuildRegistered()
    {
        lock (RegisterGate)
        {
            if (MSBuildLocator.IsRegistered)
            {
                return;
            }

#if NETFRAMEWORK
            // The net48 host loads with the Framework MSBuild that Visual Studio installs, so projects that need it load.
            var vs = MSBuildLocator.QueryVisualStudioInstances()
                .Where(i => i.DiscoveryType != DiscoveryType.DotNetSdk)
                .OrderByDescending(i => i.Version)
                .FirstOrDefault()
                ?? throw new InvalidOperationException(Strings.Get("NoVisualStudioMsBuild"));
            MSBuildLocator.RegisterInstance(vs);
#else
            var sdk = MSBuildLocator.QueryVisualStudioInstances()
                .Where(i => i.DiscoveryType == DiscoveryType.DotNetSdk && i.Version.Major == 10)
                .OrderByDescending(i => i.Version)
                .FirstOrDefault()
                ?? throw new InvalidOperationException(Strings.Get("NoDotnetSdk"));
            MSBuildLocator.RegisterInstance(sdk);
#endif
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(Solution Solution, IReadOnlyList<WorkspaceDiagnostic> Diagnostics)> OpenAsync(string solutionPath)
    {
        // The workspace is not disposed: the cached Solution keeps it alive.
        var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(solutionPath).ConfigureAwait(false);
        return (solution, workspace.Diagnostics);
    }

    /// <summary>Project file paths listed in the solution file, as full paths, in file order.</summary>
    private static IReadOnlyList<string> ReadProjectPaths(string solutionPath)
    {
        var folder = Path.GetDirectoryName(solutionPath)!;
        IEnumerable<string> entries = Path.GetExtension(solutionPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? XDocument.Load(solutionPath).Descendants("Project")
                .Select(e => (string?)e.Attribute("Path"))
                .Where(p => p != null)
                .Select(p => p!)
            : SlnProjectLine.Matches(File.ReadAllText(solutionPath)).Cast<Match>().Select(m => m.Groups[1].Value);

        return entries
            .Where(p => Path.GetExtension(p).EndsWith("proj", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFullPath(Path.Combine(folder, p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// One result per project listed in the solution file, plus any loaded project the file did not list. A project
    /// that did not load gets the diagnostics that name it, or every diagnostic when none names it.
    /// </summary>
    private static IReadOnlyList<ProjectLoadResult> BuildResults(
        string solutionPath,
        IReadOnlyList<string> listed,
        Solution solution,
        IReadOnlyList<WorkspaceDiagnostic> diagnostics)
    {
        var folder = Path.GetDirectoryName(solutionPath)!;
        var loaded = solution.Projects
            .Select(p => p.FilePath)
            .Where(f => f != null)
            .Select(f => Path.GetFullPath(f!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var all = listed
            .Concat(loaded.Where(f => !listed.Contains(f, StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        // A project that fails to load stays in Solution.Projects as a placeholder, so "loaded" alone is not enough:
        // a failure diagnostic that names the project makes it not ok.
        var failures = diagnostics
            .Where(d => d.Kind == WorkspaceDiagnosticKind.Failure)
            .Select(d => d.Message)
            .ToList();

        return all.Select(full =>
        {
            var name = RelativeName(folder, full);
            var messages = failures
                .Where(m => m.Contains(full, StringComparison.OrdinalIgnoreCase)
                            || m.Contains(Path.GetFileName(full), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (loaded.Contains(full) && messages.Count == 0)
            {
                return new ProjectLoadResult(name, true, null);
            }

            if (messages.Count == 0)
            {
                messages = failures;
            }

            var error = messages.Count > 0
                ? string.Join("; ", messages)
                : Strings.Get("ProjectNoDiagnostic");
            return new ProjectLoadResult(name, false, error);
        }).ToList();
    }

    // Path.GetRelativePath is not in .NET Framework; this gives the same result for the project paths the loader names.
    private static string RelativeName(string from, string to)
    {
        var folder = Path.GetFullPath(from).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = new Uri(Path.GetFullPath(to));
        var relative = new Uri(folder).MakeRelativeUri(target);
        return Uri.UnescapeDataString(relative.ToString()).Replace('/', Path.DirectorySeparatorChar);
    }
}
