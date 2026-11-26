using AiDevHelpers.Helpers.RoslynHelper;
using System.Text.Json;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

public class LoadSolutionActionTests
{
    [Fact]
    public async Task loadSolution_request_returns_ok_and_one_entry_per_project()
    {
        using var fixture = FixtureCopy.Create();
        var handler = new RoslynRequestHandler(() => fixture.SolutionPath);
        var request = JsonSerializer.Serialize(new { action = "loadSolution", args = new { solutionPath = fixture.SolutionPath } });

        var reply = await handler.HandleLineAsync(request);

        using var doc = JsonDocument.Parse(reply);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var projects = doc.RootElement.GetProperty("result").GetProperty("projects").EnumerateArray().ToList();
        Assert.Equal(3, projects.Count);
        Assert.Equal(2, projects.Count(p => p.GetProperty("ok").GetBoolean()));
        var failed = projects.Single(p => !p.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(failed.GetProperty("error").GetString()));
    }
}
