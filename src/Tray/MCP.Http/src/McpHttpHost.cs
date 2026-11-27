using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using AiDevHelpers.MCP.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace AiDevHelpers.MCP.Http;

/// <summary>Where the HTTP MCP listens and where it keeps its token and endpoint record.</summary>
/// <param name="Port">The loopback port; 0 picks a free one.</param>
/// <param name="TokenPath">The bearer token file (see <see cref="TokenStore"/>).</param>
/// <param name="EndpointPath">The endpoint record (pid, port, version, or the startup error).</param>
public sealed record HttpHostOptions(int Port, string TokenPath, string EndpointPath);

/// <summary>The configured port is already taken. The endpoint record carries the same cause.</summary>
public sealed class PortInUseException(int port) : Exception(Strings.Format("PortInUse", port))
{
    public int Port { get; } = port;
}

/// <summary>A started HTTP MCP. Dispose it to stop the server and remove the endpoint record.</summary>
public sealed class RunningHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _endpointPath;

    internal RunningHost(WebApplication app, int port, string token, string endpointPath)
    {
        _app = app;
        Port = port;
        Token = token;
        _endpointPath = endpointPath;
    }

    public int Port { get; }

    public string Token { get; }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try
        {
            File.Delete(_endpointPath);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Builds and starts the streamable-HTTP MCP: loopback only, stateful sessions, bearer token, Host/Origin checks.</summary>
public static class McpHttpHost
{
    /// <summary>The default loopback port.</summary>
    public const int DefaultPort = 5757;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<RunningHost> StartAsync(HttpHostOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var token = TokenStore.LoadOrCreate(options.TokenPath);
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port));
        // The SDK marks stateful mode and IdleTimeout as obsolete (SEP-2567, stateless is the default from the
        // 2026-07-28 protocol revision). Stateful stays: ControlSession, log notifications and the McpServer
        // parameters of some tools need a session. Revisit when the clients move to the newer revision.
#pragma warning disable MCP9006
        builder.Services
            .AddAiDevHelpersMcp(builder.Configuration)
            .WithHttpTransport(http =>
            {
                http.Stateless = false;
                http.IdleTimeout = TimeSpan.FromMinutes(30);
            });
#pragma warning restore MCP9006

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var origin = context.Request.Headers.Origin;
            var result = RequestGuard.Check(
                context.Request.Headers.Host.ToString(),
                origin.Count == 0 ? null : origin.ToString(),
                context.Request.Headers.Authorization.ToString(),
                context.Connection.LocalPort,
                token);
            switch (result)
            {
                case GuardResult.Allowed:
                    await next(context);
                    return;
                case GuardResult.Unauthorized:
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                default:
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
            }
        });
        app.MapGet("/healthz", (HttpContext context) => Results.Json(new
        {
            status = "ok",
            version,
            pid = Environment.ProcessId,
            port = context.Connection.LocalPort,
        }));
        app.MapMcp("/mcp");

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch (Exception ex) when (IsAddressInUse(ex))
        {
            WriteEndpoint(options.EndpointPath, new EndpointRecord(Environment.ProcessId, options.Port, version, DateTime.UtcNow, "port-in-use"));
            await app.DisposeAsync();
            throw new PortInUseException(options.Port);
        }

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var port = new Uri(address).Port;
        WriteEndpoint(options.EndpointPath, new EndpointRecord(Environment.ProcessId, port, version, DateTime.UtcNow, null));
        return new RunningHost(app, port, token, options.EndpointPath);
    }

    private static bool IsAddressInUse(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteEndpoint(string path, EndpointRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(record, JsonOptions));
    }

    private sealed record EndpointRecord(int Pid, int Port, string Version, DateTime StartedUtc, string? Error);
}
