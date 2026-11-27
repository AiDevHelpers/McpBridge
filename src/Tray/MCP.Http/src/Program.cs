// The streamable-HTTP MCP server (unit 1a of docs/plans/2026-11-26-tray-service.md). Loopback only. Host and Origin are checked, and
// every request except /healthz needs the bearer token. The tools are the same as the stdio server's.
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
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

var token = BearerToken.LoadOrCreate(Path.Combine(ProgramDataFolder(), "mcp-http", "token.txt"));
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

    // Requests from this machine's loopback address need no token. Host and Origin are still checked above.
    var fromLoopback = context.Connection.RemoteIpAddress is { } remote && System.Net.IPAddress.IsLoopback(remote);
    if (!fromLoopback && context.Request.Path != "/healthz" && !HttpGuard.Authorized(context.Request.Headers.Authorization.ToString(), token))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
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

static string ProgramDataFolder() => Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData";

/// <summary>The request checks. Pure, so the tests run them without a server.</summary>
public static class HttpGuard
{
    /// <summary>Only the loopback names; a request for any other Host name is refused (DNS rebinding).</summary>
    public static bool HostAllowed(string host) => host is "127.0.0.1" or "localhost" or "[::1]" or "::1";

    /// <summary>No browser origin is allowed except the loopback ones, so a web page cannot call the server.</summary>
    public static bool OriginAllowed(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && HostAllowed(uri.Host);

    /// <summary>True when the header is "Bearer " followed by the token, compared in constant time.</summary>
    public static bool Authorized(string? authorization, string token)
    {
        const string prefix = "Bearer ";
        if (string.IsNullOrEmpty(authorization) || !authorization.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var presented = Encoding.UTF8.GetBytes(authorization[prefix.Length..]);
        var expected = Encoding.UTF8.GetBytes(token);
        return presented.Length == expected.Length && CryptographicOperations.FixedTimeEquals(presented, expected);
    }
}

/// <summary>The bearer token: generated once, stored in a file only the current user can read.</summary>
public static class BearerToken
{
    public static string LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            return File.ReadAllText(path).Trim();
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, token);

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
        return token;
    }
}
