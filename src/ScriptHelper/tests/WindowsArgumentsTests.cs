using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class WindowsArgumentsTests
{
    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData(@"C:\repo\", "\"C:\\repo\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"a\""b", "\"a\\\\\\\"b\"")]
    [InlineData("", "\"\"")]
    public void Quote_FollowsTheWindowsRules(string token, string expected) => Assert.Equal(expected, WindowsArguments.Quote(token));

    [Theory]
    [InlineData(@"C:\repo\")]
    [InlineData("say \"hi\" there")]
    [InlineData(@"a\""b\")]
    [InlineData("-Port")]
    public void AQuotedArgument_ParsesBackToTheSameToken(string token)
    {
        // CommandLineToArgvW is how the new process splits what the shell passes it.
        var parsed = SplitLikeWindows("prog " + WindowsArguments.Quote(token));

        Assert.Equal(["prog", token], parsed);
    }

    private static string[] SplitLikeWindows(string commandLine)
    {
        var ptr = CommandLineToArgvW(commandLine, out var count);
        try
        {
            return [.. Enumerable.Range(0, count).Select(i => System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(ptr, i * IntPtr.Size))!)];
        }
        finally
        {
            LocalFree(ptr);
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string commandLine, out int argc);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
