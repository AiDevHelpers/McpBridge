using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Helpers.WslHelper;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>WSL distributions: status, health, bash commands and debug-target setup.</summary>
[McpServerToolType]
internal sealed class WslTools
{
    private static readonly WslService Service = new();

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [McpServerTool]
    [Description(
        "WSL actions. action: 'status' (distros, default, state), 'health' (OS, kernel, user, apt and sudo usable), " +
        "'run' (one bash command line via /bin/bash -c in the distro, timeoutSeconds 1-600), or 'debug_setup' " +
        "(runtime dotnet, python or node; programPath as seen inside WSL; write=true with workspaceRoot creates " +
        ".vscode/launch.json only when it does not exist). 'run' executes arbitrary commands in the distro.")]
    public static async Task<string> Wsl(
        [Description("'status', 'health', 'run' or 'debug_setup'")] string action,
        [Description("Distribution name (see status). Omit for the default distribution.")] string? distro = null,
        [Description("run: the bash command line")] string? command = null,
        [Description("run: seconds before the command is killed (1-600, default 120)")] int timeoutSeconds = 120,
        [Description("debug_setup: 'dotnet', 'python' or 'node'")] string? runtime = null,
        [Description("debug_setup: program path as seen inside WSL")] string? programPath = null,
        [Description("debug_setup: workspace folder for .vscode/launch.json")] string? workspaceRoot = null,
        [Description("debug_setup: create .vscode/launch.json when it does not exist")] bool write = false,
        CancellationToken ct = default)
    {
        try
        {
            return action.Trim().ToLowerInvariant() switch
            {
                "status" => Json(await Service.StatusAsync(ct)),
                "health" => Json(await Service.HealthAsync(distro, ct)),
                "run" => Json(await Service.RunAsync(command ?? "", distro, timeoutSeconds, ct)),
                "debug_setup" => Json(await Service.DebugSetupAsync(runtime ?? "", programPath ?? "", distro, workspaceRoot, write, ct)),
                _ => Json(new { ok = false, error = Strings.Get("WslUnknownAction") }),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, Options);
}
