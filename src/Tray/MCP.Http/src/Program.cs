// Entry point. The HTTP host, authentication and MCP wiring arrive in unit 1a of docs/plans/2026-11-26-tray-service.md.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
await app.RunAsync();
