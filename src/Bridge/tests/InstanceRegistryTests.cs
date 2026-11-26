using AiDevHelpers.Bridge;
using Xunit;

namespace AiDevHelpers.Bridge.Tests;

public sealed class InstanceRegistryTests
{
    [Fact]
    public void EventPipeNameFor_VisualStudio_KeepsVsPipeName()
    {
        Assert.Equal("AiDevHelpers.VS.42.events", InstanceRegistry.EventPipeNameFor(42));
    }

    [Fact]
    public void VsCodeEventPipeNameFor_UsesVsCodePipeName()
    {
        Assert.Equal("AiDevHelpers.VsCode.42.events", InstanceRegistry.VsCodeEventPipeNameFor(42));
    }
}
