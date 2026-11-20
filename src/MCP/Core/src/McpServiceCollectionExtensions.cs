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
        services.AddSingleton<ISessionRegistry, SessionRegistry>();
        services.AddSingleton<ITerminalConfigurationDiscovery, TerminalConfigurationDiscovery>();
        services.AddSingleton<ITerminalSessionManager, TerminalSessionManager>();
        services.AddHostedService<AiDevHelpers.MCP.Core.Services.TestMonitorService>();

        return services
            .AddMcpServer()
            .WithResources<McpDocumentationResources>()
            .WithTools<IdeTools>()
            .WithTools<ConfigTools>()
            .WithTools<ErrorTools>()
            .WithTools<ExtensionTools>()
            .WithTools<TestingTools>()
            .WithTools<TerminalSessionTools>();
    }
}
