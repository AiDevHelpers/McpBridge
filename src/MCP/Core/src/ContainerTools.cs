using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Helpers.ContainerHelper;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Docker or Podman containers: status, logs, start, stop, restart and one-shot exec.</summary>
[McpServerToolType]
internal sealed class ContainerTools
{
    private static readonly ContainerService Service = new();

    [McpServerTool]
    [Description(
        "Docker or Podman container actions. action: 'status' (state, health, exit code), 'logs' (tailLines, errorLinesOnly), " +
        "'start', 'stop', 'restart', or 'exec' (one shell command via /bin/sh -c inside the container, timeoutSeconds 1-600). " +
        "runtime picks 'docker' or 'podman'; omitted, docker is tried first, then podman. The result reports which runtime ran. " +
        "exec runs arbitrary commands inside the container.")]
    public static async Task<string> Container(
        [Description("'status', 'logs', 'start', 'stop', 'restart' or 'exec'")] string action,
        [Description("Container name or ID")] string containerName,
        [Description("logs: number of trailing lines (default 500, at least 1)")] int tailLines = 500,
        [Description("logs: return only lines that look like errors")] bool errorLinesOnly = false,
        [Description("exec: shell command, passed to /bin/sh -c")] string? command = null,
        [Description("exec: seconds before the client is killed (1-600, default 60)")] int timeoutSeconds = 60,
        [Description("'docker' or 'podman'. Omit to detect.")] string? runtime = null,
        CancellationToken ct = default)
    {
        try
        {
            switch (action.Trim().ToLowerInvariant())
            {
                case "status":
                    return Json(await Service.StatusAsync(containerName, runtime, ct));
                case "logs":
                    return Json(await Service.LogsAsync(containerName, tailLines, errorLinesOnly, runtime, ct));
                case "start":
                    return Json(await Service.StartAsync(containerName, runtime, ct));
                case "stop":
                    return Json(await Service.StopAsync(containerName, runtime, ct));
                case "restart":
                    return Json(await Service.RestartAsync(containerName, runtime, ct));
                case "exec":
                    return Json(await Service.ExecAsync(containerName, command ?? "", timeoutSeconds, runtime, ct));
                default:
                    return Json(new { ok = false, error = Strings.Get("ContainerUnknownAction") });
            }
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

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string Json(object value) => JsonSerializer.Serialize(value, Options);
}
