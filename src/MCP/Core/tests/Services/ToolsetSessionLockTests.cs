using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class ToolsetSessionLockTests
{
    [Fact]
    public void SameSessionKeepsItsFirstPath()
    {
        var locks = new ToolsetSessionLock();

        var first = locks.Resolve("chat-a", () => ToolsetPaths.VsCode);
        var later = locks.Resolve("chat-a", () => ToolsetPaths.Standalone);

        Assert.Equal(ToolsetPaths.VsCode, first);
        Assert.Equal(ToolsetPaths.VsCode, later);
    }

    [Fact]
    public void ResolverRunsOnlyOncePerSession()
    {
        var locks = new ToolsetSessionLock();
        var calls = 0;

        locks.Resolve("chat-a", () => { calls++; return ToolsetPaths.Standalone; });
        locks.Resolve("chat-a", () => { calls++; return ToolsetPaths.Standalone; });

        Assert.Equal(1, calls);
    }

    [Fact]
    public void DifferentSessionsGetTheirOwnPath()
    {
        var locks = new ToolsetSessionLock();

        var a = locks.Resolve("chat-a", () => ToolsetPaths.VsCode);
        var b = locks.Resolve("chat-b", () => ToolsetPaths.Standalone);

        Assert.Equal(ToolsetPaths.VsCode, a);
        Assert.Equal(ToolsetPaths.Standalone, b);
    }

    [Fact]
    public void MissingSessionIdsShareOneProcessKey()
    {
        var locks = new ToolsetSessionLock();

        var fromNull = locks.Resolve(null, () => ToolsetPaths.VisualStudio);
        var fromEmpty = locks.Resolve(string.Empty, () => ToolsetPaths.Standalone);

        Assert.Equal(ToolsetPaths.VisualStudio, fromNull);
        Assert.Equal(ToolsetPaths.VisualStudio, fromEmpty);
    }
}
