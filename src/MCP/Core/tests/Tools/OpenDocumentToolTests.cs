using AiDevHelpers.MCP.Core.Services;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class OpenDocumentToolTests
{
    [Fact]
    public async Task IdeOpenDocument_NoInstance_ReportsError()
    {
        var tools = new IdeTools(Microsoft.Extensions.Options.Options.Create(new IdeToolsOptions()));

        var reply = await tools.IdeOpenDocument(@"C:\__open_doc_no_such__\a.cs", 3, @"C:\__open_doc_no_such__\Missing.sln");

        Assert.Contains("\"ok\":false", reply.Replace(" ", string.Empty));
    }
}
