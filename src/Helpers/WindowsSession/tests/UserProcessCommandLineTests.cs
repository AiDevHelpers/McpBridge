using Xunit;

namespace AiDevHelpers.Helpers.WindowsSession.Tests;

public sealed class UserProcessCommandLineTests
{
    [Fact]
    public void BuildQuotesTheFileNameAndAppendsArguments()
    {
        Assert.Equal("\"C:\\Program Files\\x.exe\" --port 1", UserProcessCommandLine.Build("C:\\Program Files\\x.exe", "--port 1"));
    }

    [Fact]
    public void BuildHasNoTrailingSpaceWithoutArguments()
    {
        Assert.Equal("\"C:\\x.exe\"", UserProcessCommandLine.Build("C:\\x.exe", ""));
    }

    [Fact]
    public void BuildRejectsAQuoteInTheFileName()
    {
        Assert.Throws<ArgumentException>(() => UserProcessCommandLine.Build("C:\\a\"b.exe", ""));
    }
}
