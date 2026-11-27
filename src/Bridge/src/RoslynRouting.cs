using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiDevHelpers.Bridge;

/// <summary>One Roslyn helper's manifest, as the helper writes it to %ProgramData%\AiDevHelpers\roslyn-helpers\&lt;pid&gt;.json.</summary>
public sealed record RoslynHelperManifest(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("pipeName")] string PipeName,
    [property: JsonPropertyName("solutionPath")] string SolutionPath);

/// <summary>Finds the live Roslyn helper for a solution. A manifest whose process is gone is never picked.</summary>
public static class RoslynHelperRegistry
{
    /// <summary>The folder that holds one manifest per running helper: %ProgramData%\AiDevHelpers\roslyn-helpers.</summary>
    public static string ManifestFolder =>
        Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "AiDevHelpers", "roslyn-helpers");

    /// <summary>The first manifest for this solution whose process is alive, or null.</summary>
    public static RoslynHelperManifest? Pick(IEnumerable<RoslynHelperManifest> manifests, string solutionPath, Func<int, bool> isAlive) =>
        manifests.FirstOrDefault(m => PathCompare.Same(m.SolutionPath, solutionPath) && isAlive(m.Pid));

    /// <summary>Reads the manifest folder and picks the live helper for the solution. A missing folder means no helper.</summary>
    public static RoslynHelperManifest? FindForSolution(string solutionPath)
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

        return Pick(manifests, solutionPath, IsAlive);
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

/// <summary>Where an edit action is routed.</summary>
public enum RoslynRouteKind
{
    /// <summary>The existing IDE bridge answers the action.</summary>
    Bridge,

    /// <summary>The Roslyn helper answers the action over its pipe.</summary>
    Helper,

    /// <summary>The action belongs to the helper, but no helper runs for the solution.</summary>
    NoHelper,
}

/// <summary>Where an action goes: the bridge, the helper, or nowhere (no helper runs), with the error to report in that last case.</summary>
public sealed record RoslynRoute(RoslynRouteKind Kind, RoslynHelperManifest? Helper, string? Error);

/// <summary>Decides where an edit action goes. Only VS Code solutions with a helper-owned action go to the helper.</summary>
public static class RoslynRouting
{
    /// <summary>The edit actions the Roslyn helper answers. Keep in step with the helper's dispatcher and with VS Code's ROSLYN_ACTIONS.</summary>
    public static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "addEnumMember", "addInterfaceMember", "addMember", "addMethod", "addOverload", "addType", "changeSignature",
        "deleteMember", "deleteType", "editEnumMember", "editMember", "editMethod", "editType", "generateConstructor",
        "implementMembers", "listUnused", "moveType", "renameNamespace", "renameSymbol", "tddAddTest", "tddUpdateTest",
        "threadParameter",
    };

    /// <summary>Routes one action. Only an edit action for a VS Code solution goes to the helper; <paramref name="find"/> looks up its live helper.</summary>
    public static RoslynRoute Decide(string action, string? ide, string? solutionPath, Func<string, RoslynHelperManifest?> find)
    {
        if (!string.Equals(ide, "VsCode", StringComparison.OrdinalIgnoreCase) || !Actions.Contains(action) || solutionPath is null || string.IsNullOrWhiteSpace(solutionPath))
        {
            return new RoslynRoute(RoslynRouteKind.Bridge, null, null);
        }

        var helper = find(solutionPath);
        return helper is null
            ? new RoslynRoute(RoslynRouteKind.NoHelper, null,
                $"No Roslyn helper is running for this solution, so '{action}' cannot run. Open the solution in VS Code to start it.")
            : new RoslynRoute(RoslynRouteKind.Helper, helper, null);
    }
}
