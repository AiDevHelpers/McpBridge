using System.Text.Json;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class OpenSolutionToolTests
{
    [Fact]
    public void IdeOpenSolution_MissingFile_ReportsErrorWithoutLaunching()
    {
        using var doc = JsonDocument.Parse(IdeTools.IdeOpenSolution(@"C:\__open_sln_no_such__\Missing.sln"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }
}
