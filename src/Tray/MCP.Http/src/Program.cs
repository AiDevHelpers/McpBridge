// The streamable-HTTP MCP server (unit 1a of docs/plans/2026-11-26-tray-service.md). Loopback only. Host and Origin are checked.
// The tools are the same as the stdio server's. No token: the listener is bound to 127.0.0.1 only.
using System.Reflection;
using AiDevHelpers.MCP.Core;

var builder = WebApplication.CreateBuilder(args);
var port = builder.Configuration.GetValue("Http:Port", 5058);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Services.AddAiDevHelpersMcp(builder.Configuration).WithHttpTransport(options =>
{
    // Stateful sessions: the plan-then-confirm tokens and the log subscriptions are per session (see the plan's static-state audit).
    options.Stateless = false;
    options.IdleTimeout = TimeSpan.FromMinutes(30);
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (!HttpGuard.HostAllowed(context.Request.Host.Host))
    {
        context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
        return;
    }

    if (context.Request.Headers.TryGetValue("Origin", out var origin) && !HttpGuard.OriginAllowed(origin.ToString()))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    await next();
});

app.MapGet("/healthz", () => Results.Json(new
{
    ok = true,
    pid = Environment.ProcessId,
    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
}));
app.MapMcp("/mcp");
await app.RunAsync();

/// <summary>The request checks. Pure, so the tests run them without a server.</summary>
public static class HttpGuard
{
    /// <summary>Only the loopback names; a request for any other Host name is refused (DNS rebinding).</summary>
    public static bool HostAllowed(string host) => host is "127.0.0.1" or "localhost" or "[::1]" or "::1";

    /// <summary>No browser origin is allowed except the loopback ones, so a web page cannot call the server.</summary>
    public static bool OriginAllowed(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && HostAllowed(uri.Host);
}
