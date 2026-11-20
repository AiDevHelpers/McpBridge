using System.Text.Json;
using AiDevHelpers.MCP.Core.Services;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class OpenSolutionToolTests
{
    [Fact]
    public async Task IdeOpenSolution_NoInstance_ReportsError()
    {
        var tools = new IdeTools(Microsoft.Extensions.Options.Options.Create(new IdeToolsOptions()));

        var reply = await tools.IdeOpenSolution(@"C:\__open_sln_no_such__\Missing.sln");

        Assert.Contains("\"ok\":false", reply.Replace(" ", string.Empty));
    }
}
