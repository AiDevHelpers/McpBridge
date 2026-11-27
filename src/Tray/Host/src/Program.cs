using AiDevHelpers.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The Windows service. It starts and supervises AiDevHelpers.MCP.Http in the logged-on user's session.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "AiDevHelpers");
builder.Services.AddHostedService<SupervisorWorker>();
await builder.Build().RunAsync();
