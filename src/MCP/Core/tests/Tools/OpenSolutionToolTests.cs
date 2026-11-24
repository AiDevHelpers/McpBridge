using AiDevHelpers.MCP.Core.Services;
using System.Text.Json;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class OpenSolutionToolTests
{
    [Fact]
    public async Task IdeOpenSolution_MissingFile_ReportsErrorWithoutLaunching()
    {
        var tools = new IdeTools(Microsoft.Extensions.Options.Options.Create(new IdeToolsOptions()));
        using var doc = JsonDocument.Parse(await tools.IdeOpenSolution(@"C:\__open_sln_no_such__\Missing.sln"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }
}
