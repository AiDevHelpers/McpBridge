using AiDevHelpers.Helpers.RoslynHelper;
using System.Text.Json;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

public class RoslynRequestHandlerTests
{
    private const string Solution = @"C:\repo\App.slnx";

    [Fact]
    public async Task Health_returns_ok_true_and_the_pid()
    {
        var handler = new RoslynRequestHandler(() => Solution);

        var reply = await handler.HandleLineAsync("{\"action\":\"health\"}");

        using var doc = JsonDocument.Parse(reply);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(Environment.ProcessId, doc.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(Solution, doc.RootElement.GetProperty("solutionPath").GetString());
    }

    [Fact]
    public async Task Unknown_action_returns_ok_false_with_message()
    {
        var handler = new RoslynRequestHandler(() => Solution);

        var reply = await handler.HandleLineAsync("{\"action\":\"nope\"}");

        using var doc = JsonDocument.Parse(reply);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("Unknown action: nope.", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Malformed_line_returns_ok_false()
    {
        var handler = new RoslynRequestHandler(() => Solution);

        var reply = await handler.HandleLineAsync("{not json");

        using var doc = JsonDocument.Parse(reply);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
    }
}
