using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class EventSubscriptionsTests
{
    [Fact]
    public void EventPipeNameFor_VsCodeInstance_UsesVsCodePipeName()
    {
        Assert.Equal("AiDevHelpers.VsCode.42.events", EventSubscriptions.EventPipeNameFor(42, "VsCode"));
    }

    [Fact]
    public void EventPipeNameFor_VisualStudioInstance_UsesVsPipeName()
    {
        Assert.Equal("AiDevHelpers.VS.42.events", EventSubscriptions.EventPipeNameFor(42, "VisualStudio"));
    }

    [Fact]
    public void EventPipeNameFor_DefaultIde_UsesVsPipeName()
    {
        Assert.Equal("AiDevHelpers.VS.42.events", EventSubscriptions.EventPipeNameFor(42));
    }
}
