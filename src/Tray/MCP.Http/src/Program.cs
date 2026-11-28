// The streamable-HTTP MCP server (unit 1a of docs/plans/2026-11-26-tray-service.md). Loopback only. Host and Origin are checked.
// The tools are the same as the stdio server's. No token: the listener is bound to 127.0.0.1 only.
using System.Reflection;
using AiDevHelpers.MCP.Core;
using AiDevHelpers.MCP.Core.Services;
using AiDevHelpers.MCP.Http;
using TokenSaver.AgentHelpers.Core.Services;

// The HTTP server starts only when it is asked to serve: the service passes --serve at logon. A program launched any other way
// (a stdio client, a double click) opens no listener and says what to run instead.
if (!ServeSwitch.IsRequested(args))
{
    Console.Error.WriteLine("AiDevHelpers.MCP.Http runs the HTTP server only with --serve (the service passes it at logon). For stdio, run AiDevHelpers.MCP.");
    return 2;
}

var builder = WebApplication.CreateBuilder(ServeSwitch.Without(args));
// The shared settings file is the default; an explicit Http:Port (appsettings, environment, command line) still wins.
var port = builder.Configuration.GetValue("Http:Port", McpPortSettings.Read(McpPortSettings.DefaultPath));
var sessions = new SessionCounter();
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
    // Counts the running sessions for /healthz; the session itself runs as before. RunSessionHandler is an SDK preview API (MCPEXP002).
#pragma warning disable MCPEXP002
    options.RunSessionHandler = async (_, server, ct) =>
    {
        sessions.Enter();
        try
        {
            await server.RunAsync(ct);
        }
        finally
        {
            sessions.Exit();
        }
    };
#pragma warning restore MCPEXP002
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
    sessions = sessions.Count,
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

return 0;

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

/// <summary>The --serve switch that makes this program run as the HTTP server. Pure, so the tests run it without a server.</summary>
public static class ServeSwitch
{
    public const string Name = "--serve";

    public static bool IsRequested(string[] args) => args.Contains(Name, StringComparer.Ordinal);

    /// <summary>The arguments without the switch, so configuration does not read it as the start of a key and value pair.</summary>
    public static string[] Without(string[] args) => args.Where(a => !string.Equals(a, Name, StringComparison.Ordinal)).ToArray();
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
