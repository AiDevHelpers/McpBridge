using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiDevHelpers.MCP.Core.Services;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>
/// MCP surface for the .NET diagnostic global tools: dotnet-dump, dotnet-stack, dotnet-counters, dotnet-gcdump, dotnet-trace and
/// dotnet-symbol. Each action is a fixed operation; the caller gives a pid, seconds or a path, never a command line.
/// </summary>
[McpServerToolType]
internal sealed class DotnetDiagTools(DotnetDiagService service)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool]
    [Description(
        "Wrap the .NET diagnostic tools. action 'status' lists each tool with its path and version, or that it is missing (with the " +
        "install command). 'processes' lists .NET processes (dotnet-dump ps). 'stack' prints managed stacks for pid. 'dump' writes a " +
        "memory dump of pid. 'gcdump' writes a GC heap snapshot of pid. 'counters' and 'trace' collect for 'seconds' (1 to 300) from " +
        "pid. 'symbols' downloads symbols for the absolute 'path'. path is an optional output file for dump, gcdump, counters and trace " +
        "(default: a file under %TEMP%\\aidev-diag). Output files are written, never deleted. Nothing is installed by this tool.")]
    public async Task<string> DotnetDiag(
        string action,
        int pid = 0,
        int seconds = 5,
        string path = "",
        CancellationToken ct = default)
    {
        if (action.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            var rows = await service.StatusAsync(ct);
            var tools = rows.Select(r => new
            {
                tool = r.Tool.Executable,
                purpose = Strings.Get(r.Tool.PurposeKey),
                installed = r.Path is not null,
                path = r.Path,
                version = r.Version,
                install = r.Path is null ? Strings.Format("DiagInstallCommand", r.Tool.Package) : null,
            });
            return JsonSerializer.Serialize(new { tools }, Json);
        }

        DiagOutcome outcome = await service.RunAsync(action, pid, seconds, string.IsNullOrWhiteSpace(path) ? null : path, ct);
        return JsonSerializer.Serialize(outcome, Json);
    }
}
