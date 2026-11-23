using System.Security.Principal;
using AiDevHelpers.Common.Processes;
using Xunit;

namespace AiDevHelpers.Common.Tests;

public sealed class ProcessElevationTests
{
    [Fact]
    public void TheCurrentProcess_IsReadAsTheShellSaysItIs()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        Assert.Equal(elevated ? ElevationState.Elevated : ElevationState.NotElevated, ProcessElevation.Current());
    }

    [Fact]
    public void AProcessThatDoesNotExist_IsUnknown() => Assert.Equal(ElevationState.Unknown, ProcessElevation.Of(int.MaxValue));

    [Fact]
    public void AChildProcessOfTheSameUser_IsReadWithoutError()
    {
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;

        var state = ProcessElevation.Of(child.Id);

        Assert.Equal(ProcessElevation.Current(), state);
        child.Kill(entireProcessTree: true);
    }
}
