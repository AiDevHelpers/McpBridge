using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Helpers.ErrorHelper;
using AiDevHelpers.Helpers.LogHelper.Models;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>
/// Errors, warnings and notices from build output the caller supplies, with snapshots for "what changed since".
/// One ErrorService per solution (or one for no solution) keeps its own suppression settings and snapshots.
/// </summary>
[McpServerToolType]
internal sealed class ErrorTools
{
    private static readonly Dictionary<string, ErrorService> Services = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    [McpServerTool]
    [Description("Store build output (dotnet build or MSBuild text) so error_list and error_changes can answer from it. Replaces the previous output for the same solution.")]
    public static string ErrorIngest(
        [Description("Raw build output text")] string output,
        [Description("Optional: full path of the .sln the output came from")] string? solutionPath = null)
    {
        For(solutionPath).Ingest(output);
        return Json(new { ok = true });
    }

    [McpServerTool]
    [Description("List diagnostics from the last ingested build. kind is errors, warnings or notices. " +
                 "codes and sources are optional comma-separated filters (sources: Compiler, CodeAnalysis, NuGet, Other).")]
    public static string ErrorList(
        [Description("errors, warnings or notices")] string kind,
        [Description("Optional: comma-separated codes, e.g. CS1002,CA1822")] string? codes = null,
        [Description("Optional: comma-separated sources, e.g. NuGet,CodeAnalysis")] string? sources = null,
        [Description("Optional: full path of the .sln")] string? solutionPath = null)
    {
        if (!TryParseFilter(codes, sources, out var filter, out var error))
        {
            return Json(new { ok = false, error });
        }

        var service = For(solutionPath);
        IReadOnlyList<Diagnostic> items;
        switch (kind.ToLowerInvariant())
        {
            case "errors": items = service.GetErrors(filter); break;
            case "warnings": items = service.GetWarnings(filter); break;
            case "notices": items = service.GetNotices(filter); break;
            default: return Json(new { ok = false, error = $"Unknown kind '{kind}'. Use errors, warnings or notices." });
        }
        return Json(new { ok = true, count = items.Count, items = items.Select(Project) });
    }

    [McpServerTool]
    [Description("Record the current diagnostics under a new snapshot id. Returns only the id; call error_changes with it later.")]
    public static string ErrorSnapshot(
        [Description("Optional: full path of the .sln")] string? solutionPath = null) =>
        Json(new { ok = true, snapshotId = For(solutionPath).TakeSnapshot() });

    [McpServerTool]
    [Description("List diagnostics that appeared after the given snapshot. Optional codes and sources filters as in error_list.")]
    public static string ErrorChanges(
        [Description("Snapshot id returned by error_snapshot")] string snapshotId,
        [Description("Optional: comma-separated codes")] string? codes = null,
        [Description("Optional: comma-separated sources")] string? sources = null,
        [Description("Optional: full path of the .sln")] string? solutionPath = null)
    {
        if (!TryParseFilter(codes, sources, out var filter, out var error))
        {
            return Json(new { ok = false, error });
        }

        try
        {
            var items = For(solutionPath).GetChanges(snapshotId, filter);
            return Json(new { ok = true, count = items.Count, items = items.Select(Project) });
        }
        catch (ArgumentException ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private static ErrorService For(string? solutionPath)
    {
        var key = string.IsNullOrWhiteSpace(solutionPath) ? "" : Path.GetFullPath(solutionPath);
        lock (Gate)
        {
            if (!Services.TryGetValue(key, out var service))
            {
                var suppression = key.Length == 0 ? SuppressionSettings.Empty : SuppressionSettings.Load(key);
                service = new ErrorService(suppression);
                Services[key] = service;
            }

            return service;
        }
    }

    private static bool TryParseFilter(string? codes, string? sources, out DiagnosticFilter filter, out string? error)
    {
        var codeList = Split(codes).ToList();
        var sourceList = new List<DiagnosticSource>();
        foreach (var name in Split(sources))
        {
            if (!Enum.TryParse<DiagnosticSource>(name, ignoreCase: true, out var source))
            {
                filter = new DiagnosticFilter();
                error = $"Unknown source '{name}'. Use Compiler, CodeAnalysis, NuGet or Other.";
                return false;
            }

            sourceList.Add(source);
        }

        filter = new DiagnosticFilter(codeList, sourceList);
        error = null;
        return true;
    }

    private static IEnumerable<string> Split(string? value) =>
        (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static object Project(Diagnostic d) => new
    {
        severity = d.Severity.ToString(),
        code = d.Code,
        source = d.Source.ToString(),
        message = d.Message,
        file = d.File,
        line = d.Line,
        column = d.Column,
        project = d.Project,
    };

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
