using System.Linq;
using System.Text.Json;
using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class BuildSummaryTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static JsonElement Errors(int count, string message = "boom") =>
        Json("[" + string.Join(",", Enumerable.Range(1, count).Select(i =>
            $"{{\"severity\":\"error\",\"file\":\"a.cs\",\"line\":{i},\"column\":1,\"message\":\"{message}\"}}")) + "]");

    [Fact]
    public void Success_HasNoErrors()
    {
        var summary = JsonSerializer.SerializeToElement(BuildSummary.Build(Json("{\"succeeded\":true,\"failed_projects\":0}"), null));
        Assert.True(summary.GetProperty("succeeded").GetBoolean());
        Assert.Equal(0, summary.GetProperty("error_count").GetInt32());
        Assert.Empty(summary.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public void Failure_ListsErrorsAndCount()
    {
        var summary = JsonSerializer.SerializeToElement(BuildSummary.Build(Json("{\"succeeded\":false,\"failed_projects\":1}"), Errors(2)));
        Assert.False(summary.GetProperty("succeeded").GetBoolean());
        Assert.Equal(2, summary.GetProperty("error_count").GetInt32());
        Assert.Equal(2, summary.GetProperty("errors").GetArrayLength());
    }

    [Fact]
    public void Errors_CappedAndOmittedCounted()
    {
        var summary = JsonSerializer.SerializeToElement(BuildSummary.Build(Json("{\"succeeded\":false,\"failed_projects\":1}"), Errors(25), maxErrors: 20));
        Assert.Equal(20, summary.GetProperty("errors").GetArrayLength());
        Assert.Equal(5, summary.GetProperty("errors_omitted").GetInt32());
        Assert.Equal(25, summary.GetProperty("error_count").GetInt32());
    }

    [Fact]
    public void LongMessage_Cut()
    {
        var long200 = new string('x', 500);
        var summary = JsonSerializer.SerializeToElement(BuildSummary.Build(Json("{\"succeeded\":false,\"failed_projects\":1}"), Errors(1, long200), maxMessageChars: 200));
        var message = summary.GetProperty("errors")[0].GetProperty("message").GetString()!;
        Assert.Equal(201, message.Length);
    }

    [Fact]
    public void Warnings_NotCountedAsErrors()
    {
        var warnings = Json("[{\"severity\":\"warning\",\"file\":\"a.cs\",\"line\":1,\"column\":1,\"message\":\"w\"}]");
        var summary = JsonSerializer.SerializeToElement(BuildSummary.Build(Json("{\"succeeded\":true,\"failed_projects\":0}"), warnings));
        Assert.Equal(0, summary.GetProperty("error_count").GetInt32());
    }
}
