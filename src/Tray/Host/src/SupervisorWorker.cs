using System.Runtime.Versioning;
using AiDevHelpers.Mcp.Client;
using Microsoft.Extensions.Hosting;

namespace AiDevHelpers.Host;

/// <summary>Runs the <see cref="McpSupervisor"/> for the life of the Windows service.</summary>
[SupportedOSPlatform("windows")]
public sealed class SupervisorWorker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var folder = McpDataFolder.DefaultPath;
        var port = McpPort.Resolve();
        var exe = Path.Combine(AppContext.BaseDirectory, "AiDevHelpers.MCP.Http.exe");
        var options = new SupervisorOptions(exe, $"--Http:Port {port.ToString(System.Globalization.CultureInfo.InvariantCulture)}", AppContext.BaseDirectory);

        var supervisor = new McpSupervisor(
            options,
            new WindowsSessionSource(),
            new UserProcessLauncher(folder),
            new HttpHealthProbe(port, TimeSpan.FromSeconds(3)),
            () => ControlFile.IsClosing(),
            new SystemDelay());
        return supervisor.RunAsync(stoppingToken);
    }
}
