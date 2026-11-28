using AiDevHelpers.Helpers.TerminalControl.Contracts;
using AiDevHelpers.Helpers.TerminalControl.Core;
using AiDevHelpers.Helpers.TerminalControl.Discovery;
using AiDevHelpers.MCP.Core.Services;
using AiDevHelpers.MCP.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using TokenSaver.AgentHelpers.Anthropic;
using TokenSaver.AgentHelpers.Core.Extensions;
using TokenSaver.AgentHelpers.Core.Services;
using TokenSaver.AgentHelpers.DeepSeek;
using TokenSaver.AgentHelpers.OpenSource;
using TokenSaver.AgentHelpers.Qwen;

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
        services.Configure<ModelServerOptions>(configuration.GetSection("ModelServer"));
        services.AddSingleton<AiDevHelpers.MCP.Core.Services.ToolsetSessionLock>();
        services.Configure<ScriptWatchOptions>(configuration.GetSection("ScriptWatch"));
        services.AddSingleton<ScriptRunTracker>(_ => new ScriptRunTracker());
        services.AddSingleton(sp => new ScriptRunService(
            sp.GetRequiredService<ScriptRunTracker>(),
            new AiDevHelpers.Common.Terminals.SystemTerminalProbe(),
            () => AiDevHelpers.Common.Terminals.TerminalRegistry.ForCurrentUser().Read(),
            AiDevHelpers.Common.Processes.CommandRunner.RunAsync,
            new InteractiveScriptRunner(sp.GetRequiredService<ITerminalSessionManager>()).RunAsync,
            (root, script, args, ct) => sp.GetRequiredService<IElevVisibleRunner>().RunVisibleAsync(root, script, args, ct)));
        if (RepoSettings.GlobalAllowAllWarning(configuration) is { } globalAllowAll)
        {
            Console.Error.WriteLine(globalAllowAll);
        }

        services.AddSingleton<ISessionRegistry, SessionRegistry>();
        services.AddSingleton<ITerminalConfigurationDiscovery, TerminalConfigurationDiscovery>();
        services.AddSingleton<ITerminalSessionManager, TerminalSessionManager>();
        services.AddHostedService<AiDevHelpers.MCP.Core.Services.TestMonitorService>();
        services.AddHostedService<AiDevHelpers.MCP.Core.Services.ServerControlService>();
        services.AddHostedService<AiDevHelpers.MCP.Core.Services.BridgeSettingsService>();
        services.AddSingleton<ProcessElevRunner>();
        services.AddSingleton<IElevRunner>(sp => sp.GetRequiredService<ProcessElevRunner>());
        services.AddSingleton<IElevVerifier>(sp => sp.GetRequiredService<ProcessElevRunner>());
        services.AddSingleton<IElevScanner>(sp => sp.GetRequiredService<ProcessElevRunner>());
        services.AddSingleton<IElevInstaller>(sp => sp.GetRequiredService<ProcessElevRunner>());
        services.AddSingleton<IElevVisibleRunner>(sp => sp.GetRequiredService<ProcessElevRunner>());
        services.AddSingleton(sp => new ToolInstallService(
            sp.GetRequiredService<IElevInstaller>(),
            new AiDevHelpers.Common.Terminals.SystemTerminalProbe(),
            AiDevHelpers.Common.Terminals.TerminalRegistry.ForCurrentUser()));
        services.AddSingleton(_ => new AiDevHelpers.MCP.Core.Services.DotnetDiagService());
        services.AddSingleton(_ => new AiDevHelpers.Helpers.SourceControlHelper.GitHubService(new AiDevHelpers.Helpers.SourceControlHelper.ProcessGhRunner()));
        services.AddHostedService<AiDevHelpers.MCP.Core.Services.RepoOpenService>();
        services.AddHostedService<AiDevHelpers.MCP.Core.Services.EventAutoSubscribeService>();

        // Caller-aware formatting (AiDevHelpers.AgentHelpers): the model adapter and client adapter for each caller.
        services.AddAgentHelpers()
            .AddAdapter<AnthropicAgentAdapter>()
            .AddAdapter<DeepSeekAgentAdapter>()
            .AddAdapter<QwenAgentAdapter>()
            .AddAdapter<OpenSourceAgentAdapter>()
            .AddAdapter<CopilotClientAdapter>()
            .AddAdapter<CursorClientAdapter>()
            .AddAdapter<ContinueClientAdapter>();

        return services
            .AddMcpServer()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, ct) =>
            {
                // Remember this chat's server so a control change can be pushed to it, and refuse calls while not running.
                AiDevHelpers.MCP.Core.Services.ControlSession.Remember(context.Server);
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

                // A token planned in another chat session cannot be confirmed here.
                var sessionId = context.Server.SessionId ?? string.Empty;
                if (context.Params?.Arguments is { } arguments
                    && arguments.TryGetValue("token", out var suppliedToken)
                    && suppliedToken.ValueKind == System.Text.Json.JsonValueKind.String
                    && !PlanOwnership.MayUse(suppliedToken.GetString() ?? string.Empty, sessionId))
                {
                    return new ModelContextProtocol.Protocol.CallToolResult
                    {
                        IsError = true,
                        Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = Strings.Get("TokenOtherSession") }],
                    };
                }

                // Lock this session to its repo's toolset path at the first call that finds a repo. Routing by the locked path comes next.
                var toolsetLocks = context.Services?.GetService<AiDevHelpers.MCP.Core.Services.ToolsetSessionLock>();
                var toolsetOptions = context.Services?.GetService<Microsoft.Extensions.Options.IOptions<AiDevHelpers.MCP.Core.Services.IdeToolsOptions>>();
                // The path this call runs on: the locked one, or the server default while no repo is found.
                var toolsetPath = AiDevHelpers.MCP.Core.Services.ToolsetPaths.Standalone;
                AiDevHelpers.MCP.Core.Services.ToolsetLock? toolsetLock = null;
                if (toolsetLocks is not null && toolsetOptions is not null)
                {
                    var locked = AiDevHelpers.MCP.Core.Services.ToolsetRepoDiscovery.LockFor(
                        toolsetLocks,
                        sessionId,
                        toolsetOptions.Value,
                        AiDevHelpers.MCP.Core.Services.ToolsetRepoDiscovery.ExplicitRepoArgument(context.Params?.Arguments),
                        Directory.GetCurrentDirectory());
                    toolsetLock = locked;
                    toolsetPath = locked?.Path ?? AiDevHelpers.MCP.Core.Services.ToolsetPaths.Normalize(toolsetOptions.Value.ToolsetPath);
                }

                var routingRefusal = AiDevHelpers.MCP.Core.Services.ToolsetRouting.RefusalFor(context.Params?.Name, toolsetPath);
                if (routingRefusal is not null)
                {
                    return new ModelContextProtocol.Protocol.CallToolResult
                    {
                        IsError = true,
                        Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = routingRefusal }],
                    };
                }

                // file_edit on an IDE path runs as the bridge's replaceInFile action, in the locked repo.
                if (context.Params?.Name == "file_edit" && toolsetPath != AiDevHelpers.MCP.Core.Services.ToolsetPaths.Standalone)
                {
                    var editArguments = context.Params?.Arguments ?? new Dictionary<string, System.Text.Json.JsonElement>();
                    var mapping = AiDevHelpers.MCP.Core.Services.ToolsetFileEditMapper.Map(editArguments);
                    if (mapping.Refusal is not null)
                    {
                        return new ModelContextProtocol.Protocol.CallToolResult
                        {
                            IsError = true,
                            Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = mapping.Refusal }],
                        };
                    }

                    if (toolsetLock is not null)
                    {
                        mapping.Args["root"] = toolsetLock.RepoRoot;
                    }

                    var reply = await AiDevHelpers.Bridge.IdeInstanceRouter.CallAsync(mapping.Action, mapping.Args, null, ct, toolsetPath);
                    return new ModelContextProtocol.Protocol.CallToolResult
                    {
                        Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = reply }],
                    };
                }

                // A dangerous tool needs the person's yes on the tray for this exact call. Only a host that registers a channel (HTTP) asks;
                // stdio registers none and keeps its behaviour.
                var approvalChannel = context.Services?.GetService<IApprovalChannel>();
                var toolName = context.Params?.Name;
                if (approvalChannel is not null && ToolClassification.IsDangerous(toolName))
                {
                    var argumentsJson = System.Text.Json.JsonSerializer.Serialize(context.Params?.Arguments);
                    if (!await ApprovalGate.ApproveAsync(approvalChannel, toolName!, argumentsJson, () => DateTime.UtcNow, ct).ConfigureAwait(false))
                    {
                        return new ModelContextProtocol.Protocol.CallToolResult
                        {
                            IsError = true,
                            Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = Strings.Get("ApprovalRefused") }],
                        };
                    }
                }

                var result = await next(context, ct);
                foreach (var block in result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>())
                {
                    PlanOwnership.RecordFrom(block.Text, sessionId);
                }

                // Fit the text to this caller's token budget (AgentHelpers). Runs after the plan tokens are recorded, so a cut never hides one.
                var registry = context.Services?.GetService<AgentAdapterRegistry>();
                if (registry is not null)
                {
                    var caller = AgentFormatting.CallerFrom(context.Server.ClientInfo?.Name, context.Server.ClientInfo?.Version, null, null);
                    var format = AgentFormatting.FormatFor(registry, caller);
                    result.Content = result.Content
                        .Select(block => block is ModelContextProtocol.Protocol.TextContentBlock text
                            ? new ModelContextProtocol.Protocol.TextContentBlock { Text = AgentFormatting.Fit(text.Text, format) }
                            : block)
                        .ToList();
                }

                return result;
            }))
            .WithResources<McpDocumentationResources>()
            .WithTools<IdeTools>()
            .WithTools<ConfigTools>()
            .WithTools<ErrorTools>()
            .WithTools<ExtensionTools>()
            .WithTools<TestingTools>()
            .WithTools<BookmarkTools>()
            .WithTools<ModelServerTools>()
            .WithTools<DiffSummarizeTools>()
            .WithTools<FileEditTools>()
            .WithTools<ContainerTools>()
            .WithTools<WslTools>()
            .WithTools<LintTools>()
            .WithTools<LocalizationTools>()
            .WithTools<PreferredIdeTools>()
            .WithTools<SourceControlTools>()
            .WithTools<IdeResetTools>()
            .WithTools<TestHostTools>()
            .WithTools<OutputTools>()
            .WithTools<SelfTestTools>()
            .WithTools<GateTools>()
            .WithTools<ServerControlTools>()
            .WithTools<TerminalSessionTools>()
            .WithTools<ScriptHelperTools>()
            .WithTools<RepoSetupTools>()
            .WithTools<ScriptRunTools>()
            .WithTools<ToolInstallTools>()
            .WithTools<DotnetDiagTools>();
    }
}

/// <summary>
/// Marks an MCP tool that only reads or changes nothing outside the chat. Every tool carries exactly one of <see cref="SafeToolAttribute"/>
/// or <see cref="DangerousToolAttribute"/>; a test fails for a tool with neither.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SafeToolAttribute : Attribute
{
}

/// <summary>
/// Marks an MCP tool that runs commands, kills processes or changes the machine. Over HTTP it needs the person's confirmation
/// before it runs (unit 1c of docs/plans/2026-11-26-tray-service.md).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DangerousToolAttribute : Attribute
{
}
