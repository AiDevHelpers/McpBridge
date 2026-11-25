using AiDevHelpers.Helpers.GateHelper;
using Xunit;

namespace AiDevHelpers.Helpers.GateHelper.Tests;

public sealed class BuildSummaryTests
{
    private sealed class FixedOutputRunner(string output) : IGateRunner
    {
        public Task<(int ExitCode, string Output)> RunAsync(GateStep step, string workingDirectory, CancellationToken ct = default) =>
            Task.FromResult((1, output));
    }

    [Fact]
    public async Task EachDiagnosticIsCountedOnceEvenWhenMSBuildRepeatsIt()
    {
        const string line = @"C:\repo\Probe\Warn.cs(6,13): error CS0219: The variable 'unused' is assigned but its value is never used [C:\repo\Probe\Probe.csproj]";
        var output = $"{line}\r\nBuild FAILED.\r\n{line}\r\n    0 Warning(s)\r\n    1 Error(s)\r\n";

        var results = await GateRunner.RunAsync(
            [new GateStep("dotnet build", "dotnet", ["build", "App.sln"])],
            @"C:\repo",
            new FixedOutputRunner(output));

        Assert.Contains("Probe.csproj: 1 errors, 0 warnings", results[0].Output);
    }
}
