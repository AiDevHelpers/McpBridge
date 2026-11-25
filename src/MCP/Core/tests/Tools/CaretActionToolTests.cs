using AiDevHelpers.MCP.Core.Services;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class CaretActionToolTests
{
    private static IdeTools Tools() => new(Microsoft.Extensions.Options.Options.Create(new IdeToolsOptions()));

    [Fact]
    public async Task GoToDefinition_NoInstance_ReportsError()
    {
        var reply = await Tools().IdeGoToDefinition(@"C:\__caret_no_such__\a.cs", 3, 1, @"C:\__caret_no_such__\Missing.sln");

        Assert.Contains("\"ok\":false", reply.Replace(" ", string.Empty));
    }

    [Fact]
    public async Task FindAllReferences_NoInstance_ReportsError()
    {
        var reply = await Tools().IdeFindAllReferences(@"C:\__caret_no_such__\a.cs", 3, 1, @"C:\__caret_no_such__\Missing.sln");

        Assert.Contains("\"ok\":false", reply.Replace(" ", string.Empty));
    }
}
