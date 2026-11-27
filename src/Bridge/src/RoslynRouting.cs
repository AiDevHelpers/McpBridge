using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiDevHelpers.Bridge;

/// <summary>One Roslyn helper's manifest, as the helper writes it to %ProgramData%\AiDevHelpers\roslyn-helpers\&lt;pid&gt;.json.
/// <c>Framework</c> is <c>net10</c> or <c>net48</c>; a manifest without it is a net10 helper.</summary>
public sealed record RoslynHelperManifest(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("pipeName")] string PipeName,
    [property: JsonPropertyName("solutionPath")] string SolutionPath,
    [property: JsonPropertyName("framework")] string Framework = "net10");

/// <summary>Finds the live Roslyn helper for a solution and framework. A manifest whose process is gone is never picked.</summary>
public static class RoslynHelperRegistry
{
    /// <summary>The folder that holds one manifest per running helper: %ProgramData%\AiDevHelpers\roslyn-helpers.</summary>
    public static string ManifestFolder =>
        Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "AiDevHelpers", "roslyn-helpers");

    /// <summary>The first manifest for this solution and framework whose process is alive, or null.</summary>
    public static RoslynHelperManifest? Pick(IEnumerable<RoslynHelperManifest> manifests, string solutionPath, string framework, Func<int, bool> isAlive) =>
        manifests.FirstOrDefault(m =>
            PathCompare.Same(m.SolutionPath, solutionPath)
            && string.Equals(m.Framework, framework, StringComparison.OrdinalIgnoreCase)
            && isAlive(m.Pid));

    /// <summary>Reads the manifest folder and picks the live helper for the solution and framework. A missing folder means none.</summary>
    public static RoslynHelperManifest? FindForSolution(string solutionPath, string framework)
    {
        var manifests = new List<RoslynHelperManifest>();
        var folder = ManifestFolder;
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    var manifest = JsonSerializer.Deserialize<RoslynHelperManifest>(File.ReadAllText(file));
                    if (manifest is not null)
                    {
                        manifests.Add(manifest);
                    }
                }
                catch (IOException)
                {
                    // A manifest being written or removed right now is skipped; the next call sees it.
                }
                catch (JsonException)
                {
                    // A damaged manifest is skipped.
                }
            }
        }

        // A manifest whose process is gone is left behind by a killed helper; remove it so the folder does not fill up.
        foreach (var file in Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.json") : Enumerable.Empty<string>())
        {
            var stale = manifests.FirstOrDefault(m => Path.GetFileName(file) == m.Pid + ".json");
            if (stale is not null && !IsAlive(stale.Pid))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // In use right now; the next call tries again.
                }
            }
        }

        return Pick(manifests, solutionPath, framework, IsAlive);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public enum RoslynRouteKind
{
    /// <summary>The existing IDE bridge answers the action.</summary>
    Bridge,

    /// <summary>A Roslyn helper answers the action over its pipe.</summary>
    Helper,

    /// <summary>The action belongs to a helper, but no helper of the needed framework runs for the solution.</summary>
    NoHelper,

    /// <summary>The action touches projects of both frameworks, which no single helper can load.</summary>
    Refused,
}

/// <summary>Where an edit action is routed.</summary>
public sealed record RoslynRoute(RoslynRouteKind Kind, RoslynHelperManifest? Helper, string? Error);

/// <summary>Decides where an edit action goes. Only VS Code solutions with a helper-owned action go to a helper, and the
/// helper is the one for the framework of the project the action touches.</summary>
public static class RoslynRouting
{
    /// <summary>The edit actions the Roslyn helpers answer. Keep in step with the helpers' dispatcher and with VS Code's ROSLYN_ACTIONS.</summary>
    public static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "addEnumMember", "addInterfaceMember", "addMember", "addMethod", "addOverload", "addType", "changeSignature",
        "deleteMember", "deleteType", "editEnumMember", "editMember", "editMethod", "editType", "generateConstructor",
        "implementMembers", "listUnused", "moveType", "renameNamespace", "renameSymbol", "tddAddTest", "tddUpdateTest",
        "threadParameter",
    };

    /// <summary>Routes one action. <paramref name="find"/> looks up the live helper for a solution and framework.</summary>
    public static RoslynRoute Decide(string action, string? ide, string? solutionPath, string? file, Func<string, string, RoslynHelperManifest?> find) =>
        DecideAcross(action, ide, solutionPath, file is null ? Array.Empty<string>() : new[] { file }, find);

    /// <summary>Routes one action that may touch several files. Files in projects of both frameworks are refused: one helper cannot load both.</summary>
    public static RoslynRoute DecideAcross(string action, string? ide, string? solutionPath, IEnumerable<string> files, Func<string, string, RoslynHelperManifest?> find)
    {
        if (!string.Equals(ide, "VsCode", StringComparison.OrdinalIgnoreCase) || !Actions.Contains(action)
            || solutionPath is null || string.IsNullOrWhiteSpace(solutionPath))
        {
            return new RoslynRoute(RoslynRouteKind.Bridge, null, null);
        }

        var frameworks = files.Select(FrameworkForFile).Distinct().ToList();
        if (frameworks.Count > 1)
        {
            return new RoslynRoute(RoslynRouteKind.Refused, null,
                $"'{action}' touches projects of both net10 and net48. Split it into one action per framework.");
        }

        var framework = frameworks.FirstOrDefault() ?? "net10";
        var helper = find(solutionPath, framework);
        return helper is null
            ? new RoslynRoute(RoslynRouteKind.NoHelper, null,
                $"No {framework} Roslyn helper is running for this solution, so '{action}' cannot run. Open the solution in VS Code to start it.")
            : new RoslynRoute(RoslynRouteKind.Helper, helper, null);
    }

    /// <summary>The framework of the project a file belongs to: the nearest .csproj above it. No project, or an SDK-style net10
    /// project, is net10; a legacy project or one that targets net48 is net48.</summary>
    public static string FrameworkForFile(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return "net10";
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        while (!string.IsNullOrEmpty(dir))
        {
            var project = Directory.EnumerateFiles(dir, "*.csproj").FirstOrDefault();
            if (project is not null)
            {
                return Classify(File.ReadAllText(project));
            }

            dir = Path.GetDirectoryName(dir);
        }

        return "net10";
    }

    /// <summary>Classifies project file text: <c>net48</c> for a legacy project or one that targets net48, else <c>net10</c>.</summary>
    public static string Classify(string projectText)
    {
        var legacy = !projectText.Contains("Sdk=", StringComparison.OrdinalIgnoreCase) || projectText.Contains("<TargetFrameworkVersion>", StringComparison.OrdinalIgnoreCase);
        var net48 = projectText.Contains("<TargetFramework>net48</TargetFramework>", StringComparison.OrdinalIgnoreCase);
        return legacy || net48 ? "net48" : "net10";
    }
}
