using Microsoft.Extensions.Hosting;

// Entry point. The MCP supervisor arrives in unit 2 of docs/plans/2026-11-26-tray-service.md.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "AiDevHelpers");
await builder.Build().RunAsync();
