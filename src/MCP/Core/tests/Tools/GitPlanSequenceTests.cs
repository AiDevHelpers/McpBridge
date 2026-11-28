using System.Text.Json;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class GitPlanSequenceTests
{
    [Fact]
    public async Task SequenceWithoutAction_IsPlanned()
    {
        var repo = Path.Combine(Path.GetTempPath(), "gitplan-seq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            File.WriteAllText(Path.Combine(repo, "a.txt"), "one");
            var json = await SourceControlTools.GitPlan(repo, "", paths: ["a.txt"], message: "m", sequence: ["stage", "commit"]);

            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), json);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }
}
