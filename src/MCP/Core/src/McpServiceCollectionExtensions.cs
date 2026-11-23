using AiDevHelpers.Helpers.TerminalControl.Contracts;
using AiDevHelpers.Helpers.TerminalControl.Core;
using AiDevHelpers.Helpers.TerminalControl.Discovery;
using AiDevHelpers.MCP.Core.Services;
using AiDevHelpers.MCP.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core;

/// <summary>Registers the IDE bridge MCP tools, resources and terminal services with a host.</summary>
public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// Adds the bridge MCP server with a stdio transport (what the AiDevHelpers.MCP exe uses).
    /// </summary>
    public static IHostApplicationBuilder AddAiDevHelpersMcp(this IHostApplicationBuilder builder)
    {
        builder.Services.AddAiDevHelpersMcp(builder.Configuration).WithStdioServerTransport();
        return builder;
    }

    /// <summary>
    /// Adds the bridge tools, resources and terminal services and returns the MCP builder without a transport,
    /// so another MCP host can choose its own transport and add its own tools.
    /// </summary>
    public static IMcpServerBuilder AddAiDevHelpersMcp(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IdeToolsOptions>(configuration.GetSection("IdeTools"));
        if (RepoSettings.GlobalAllowAllWarning(configuration) is { } globalAllowAll)
        {
            Console.Error.WriteLine(globalAllowAll);
        }

        services.AddSingleton<ISessionRegistry, SessionRegistry>();
        services.AddSingleton<ITerminalConfigurationDiscovery, TerminalConfigurationDiscovery>();
        services.AddSingleton<ITerminalSessionManager, TerminalSessionManager>();
        services.AddHostedService<AiDevHelpers.MCP.Core.Services.TestMonitorService>();
        services.AddHostedService<AiDevHelpers.MCP.Core.Services.ServerControlService>();
        services.AddSingleton<IElevRunner, ProcessElevRunner>();

        return services
            .AddMcpServer()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, ct) =>
            {
                // Remember this chat's server so a control change can be pushed to it, and refuse calls while not running.
                AiDevHelpers.MCP.Core.Services.ControlSession.Server = context.Server;
                var refusal = AiDevHelpers.MCP.Core.Services.ServerControlGate.Refusal(
                    context.Params?.Name,
                    AiDevHelpers.MCP.Core.Services.ServerControl.Read());
                if (refusal is not null)
                {
                    return new ModelContextProtocol.Protocol.CallToolResult
                    {
                        IsError = true,
                        Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = refusal }],
                    };
                }

                return await next(context, ct);
            }))
            .WithResources<McpDocumentationResources>()
            .WithTools<IdeTools>()
            .WithTools<ConfigTools>()
            .WithTools<ErrorTools>()
            .WithTools<ExtensionTools>()
            .WithTools<TestingTools>()
            .WithTools<BookmarkTools>()
            .WithTools<LintTools>()
            .WithTools<PreferredIdeTools>()
            .WithTools<SourceControlTools>()
            .WithTools<OutputTools>()
            .WithTools<SelfTestTools>()
            .WithTools<GateTools>()
            .WithTools<ServerControlTools>()
            .WithTools<TerminalSessionTools>()
            .WithTools<ScriptHelperTools>();
    }
}
