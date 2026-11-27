using AiDevHelpers.MCP.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TokenSaver.AgentHelpers.Anthropic;
using TokenSaver.AgentHelpers.Core.Contracts;
using TokenSaver.AgentHelpers.Core.Extensions;
using TokenSaver.AgentHelpers.Core.Models;
using TokenSaver.AgentHelpers.Core.Services;
using TokenSaver.AgentHelpers.OpenSource;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class AgentFormattingTests
{
    private static AgentAdapterRegistry BuildRegistry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentHelpers()
            .AddAdapter<AnthropicAgentAdapter>()
            .AddAdapter<CursorClientAdapter>();
        return services.BuildServiceProvider().GetRequiredService<AgentAdapterRegistry>();
    }

    [Fact]
    public void CallerFrom_LowercasesHint_AndMarksMcpClientInfoSource()
    {
        var caller = AgentFormatting.CallerFrom("claude-code", "1.0", "Claude-Sonnet-4-6", null);

        Assert.Equal("claude-code", caller.ClientName);
        Assert.Equal("claude-sonnet-4-6", caller.ModelHint);
        Assert.Equal(DetectionSource.McpClientInfo, caller.Source);
    }

    [Fact]
    public void CallerFrom_NoClientName_FallsBackToDefaultSource()
    {
        var caller = AgentFormatting.CallerFrom(null, null, null, "   ");

        Assert.Equal(string.Empty, caller.ClientName);
        Assert.Null(caller.ModelHint);
        Assert.Null(caller.UserAgent);
        Assert.Equal(DetectionSource.Default, caller.Source);
    }

    [Fact]
    public void FormatFor_ReturnsPositiveBudget_ForKnownAndUnknownCallers()
    {
        var registry = BuildRegistry();

        var claude = AgentFormatting.FormatFor(registry, AgentFormatting.CallerFrom("claude-code", null, "claude-sonnet-4-6", null));
        var unknown = AgentFormatting.FormatFor(registry, AgentFormatting.CallerFrom(null, null, null, null));

        Assert.True(claude.ContextTokenBudget > 0);
        Assert.True(unknown.ContextTokenBudget > 0);
    }

    [Fact]
    public void Fit_LeavesShortText_Unchanged()
    {
        const string text = "build succeeded";

        Assert.Equal(text, AgentFormatting.Fit(text, new ContextFormat { ContextTokenBudget = 1_000 }));
    }

    [Fact]
    public void Fit_TrimsLongText_ToWithinBudget()
    {
        var format = new ContextFormat { ContextTokenBudget = 50 };
        var text = string.Concat(Enumerable.Repeat("error CS1002: ; expected at line 42.\n", 500));

        var fitted = AgentFormatting.Fit(text, format);

        // The cut text fits the budget; the trim notice that follows it tells the caller the reply was cut.
        Assert.True(fitted.Length < text.Length);
        Assert.Contains("context trimmed", fitted);
        Assert.True(fitted.Length <= format.ContextTokenBudget * 4 + 80);
    }

    [Fact]
    public void Instructions_NameTheBudget()
    {
        var text = AgentFormatting.Instructions(new ContextFormat { ContextTokenBudget = 12_345 });

        Assert.Contains("12345", text);
    }
}

