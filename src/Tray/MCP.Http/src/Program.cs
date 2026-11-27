// The streamable-HTTP MCP server (unit 1a of docs/plans/2026-11-26-tray-service.md). Loopback only. Host and Origin are checked.
// The tools are the same as the stdio server's. No token: the listener is bound to 127.0.0.1 only.
using System.Reflection;
using AiDevHelpers.MCP.Core;
using AiDevHelpers.MCP.Core.Services;
using TokenSaver.AgentHelpers.Core.Services;

var builder = WebApplication.CreateBuilder(args);
var port = builder.Configuration.GetValue("Http:Port", 5058);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Services.AddAiDevHelpersMcp(builder.Configuration).WithHttpTransport(options =>
{
    // Stateful sessions: the plan-then-confirm tokens and the log subscriptions are per session (see the plan's static-state audit).
    options.Stateless = false;
    // Each session gets the server instructions for its caller (AiDevHelpers.AgentHelpers), from the request headers.
    options.ConfigureSessionOptions = (httpContext, serverOptions, _) =>
    {
        var registry = httpContext.RequestServices.GetRequiredService<AgentAdapterRegistry>();
        var caller = AgentFormatting.CallerFrom(
            null,
            null,
            httpContext.Request.Headers["X-Model-Hint"].FirstOrDefault() ?? httpContext.Request.Headers["X-LLM-Model"].FirstOrDefault(),
            httpContext.Request.Headers.UserAgent.ToString());
        serverOptions.ServerInstructions = AgentFormatting.Instructions(AgentFormatting.FormatFor(registry, caller));
        return Task.CompletedTask;
    };
    // A session with no requests for this long is dropped; the client re-initializes. Configurable for tests (Http:IdleTimeoutMinutes).
    options.IdleTimeout = TimeSpan.FromMinutes(builder.Configuration.GetValue("Http:IdleTimeoutMinutes", 30.0));
});

// Dangerous tools need the person's yes on the tray, over the per-user approval pipe. With no tray running there is no pipe, the
// connect fails, and the call is refused (fail closed).
builder.Services.AddSingleton<IApprovalChannel>(new ApprovalPipeChannel(ApprovalWire.PipeNameForCurrentUser()));
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
try
{
    await app.RunAsync();
}
catch (IOException ex)
{
    // The listener could not bind (usually the port is taken). No /healthz can answer, so the cause goes to a file the tray reads.
    var folder = Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "AiDevHelpers", "mcp-http");
    Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "status.json"), PortConflictReport.Json(port, ex.Message));
    throw;
}

/// <summary>The status file written when the listener cannot bind. Pure, so the tests check its shape without a server.</summary>
public static class PortConflictReport
{
    public static string Json(int port, string cause) => System.Text.Json.JsonSerializer.Serialize(new
    {
        ok = false,
        state = "portConflict",
        port,
        cause,
        atUtc = DateTime.UtcNow,
    });
}

/// <summary>The request checks. Pure, so the tests run them without a server.</summary>
public static class HttpGuard
{
    /// <summary>Only the loopback names; a request for any other Host name is refused (DNS rebinding).</summary>
    public static bool HostAllowed(string host) => host is "127.0.0.1" or "localhost" or "[::1]" or "::1";

    /// <summary>No browser origin is allowed except the loopback ones, so a web page cannot call the server.</summary>
    public static bool OriginAllowed(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && HostAllowed(uri.Host);
}
