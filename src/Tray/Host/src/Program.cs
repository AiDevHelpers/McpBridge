// Spike A (throwaway, docs/plans/2026-11-26-tray-service.md). A LocalSystem service that starts the MCP.Http spike in the console
// user's session and logs what it did. The MCP.Http path is fixed for this spike; the real supervisor is unit 2.
using AiDevHelpers.Helpers.WindowsSession;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "AiDevHelpers");
builder.Services.AddHostedService<SpikeWorker>();
await builder.Build().RunAsync();

internal sealed class SpikeWorker(ILogger<SpikeWorker> log) : BackgroundService
{
    private const string Exe = @"F:\source\McpBridge\src\Tray\MCP.Http\src\bin\Release\net10.0-windows\AiDevHelpers.MCP.Http.exe";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var session = UserSession.ActiveConsoleSession();
        log.LogInformation("Console session {Session}; starting {Exe}", session, Exe);
        var process = UserProcess.Start(new UserProcessStartInfo(Exe, string.Empty, session, Path.GetDirectoryName(Exe)));
        log.LogInformation("Started pid {Pid}", process?.Id);
        // Stay running until the service stops. A BackgroundService that returns would end the host.
        return Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
