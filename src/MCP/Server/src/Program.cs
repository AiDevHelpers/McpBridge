using AiDevHelpers.MCP.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// The IDE bridge's MCP server. It speaks MCP over stdio. All logic lives in AiDevHelpers.MCP.Core.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.AddAiDevHelpersMcp();

await builder.Build().RunAsync();
