using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class EventSeverityMapTests
{
    private static string Line(string name, string data) =>
        $"{{\"event\":\"{name}\",\"seq\":1,\"data\":{data}}}";

    [Fact]
    public void Dropped_IsWarning() =>
        Assert.Equal(EventSeverity.Warning, EventSeverityMap.Of("dropped", Line("dropped", "{\"count\":3}")));

    [Fact]
    public void EnterBreakMode_ExceptionNotHandled_IsError() =>
        Assert.Equal(EventSeverity.Error,
            EventSeverityMap.Of("enterBreakMode", Line("enterBreakMode", "{\"reason\":\"dbgEventReasonExceptionNotHandled\"}")));

    [Fact]
    public void EnterBreakMode_ExceptionThrown_IsWarning() =>
        Assert.Equal(EventSeverity.Warning,
            EventSeverityMap.Of("enterBreakMode", Line("enterBreakMode", "{\"reason\":\"dbgEventReasonExceptionThrown\"}")));

    [Theory]
    [InlineData("dbgEventReasonBreakpoint")]
    [InlineData("dbgEventReasonStepCompleted")]
    [InlineData("dbgEventReasonUserBreak")]
    public void EnterBreakMode_OtherReasons_AreInfo(string reason) =>
        Assert.Equal(EventSeverity.Info,
            EventSeverityMap.Of("enterBreakMode", Line("enterBreakMode", $"{{\"reason\":\"{reason}\"}}")));

    [Fact]
    public void EnterBreakMode_NoReason_IsInfo() =>
        Assert.Equal(EventSeverity.Info, EventSeverityMap.Of("enterBreakMode", Line("enterBreakMode", "{}")));

    [Theory]
    [InlineData("buildDone")]
    [InlineData("buildBegin")]
    [InlineData("enterRunMode")]
    [InlineData("contextChanged")]
    [InlineData("solutionOpened")]
    [InlineData("solutionClosing")]
    [InlineData("documentSaved")]
    [InlineData("documentOpened")]
    [InlineData("documentClosing")]
    [InlineData("someFutureEvent")]
    public void OtherNames_AreInfo(string name) =>
        Assert.Equal(EventSeverity.Info, EventSeverityMap.Of(name, Line(name, "null")));

    [Fact]
    public void MalformedLine_IsInfo() =>
        Assert.Equal(EventSeverity.Info, EventSeverityMap.Of("enterBreakMode", "{ not json"));

    [Theory]
    [InlineData("info", EventSeverity.Info)]
    [InlineData("WARNING", EventSeverity.Warning)]
    [InlineData(" Error ", EventSeverity.Error)]
    public void TryParse_AcceptsThreeNamesAnyCase(string text, EventSeverity expected)
    {
        Assert.True(EventSeverityMap.TryParse(text, out var severity));
        Assert.Equal(expected, severity);
    }

    [Fact]
    public void TryParse_Null_IsInfo()
    {
        Assert.True(EventSeverityMap.TryParse(null, out var severity));
        Assert.Equal(EventSeverity.Info, severity);
    }

    [Fact]
    public void TryParse_Garbage_ReturnsFalse() =>
        Assert.False(EventSeverityMap.TryParse("loud", out _));
}
