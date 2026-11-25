using AiDevHelpers.Helpers.PackageHelper;
using Xunit;

namespace AiDevHelpers.Helpers.PackageHelper.Tests;

public sealed class PackageRestoreTests
{
    private sealed class FakeRunner(int exit, string output) : IPackageRunner
    {
        public List<string> Calls { get; } = [];

        public string? WorkingDirectory { get; private set; }

        public Task<(int ExitCode, string Output)> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            WorkingDirectory = workingDirectory;
            Calls.Add(string.Join(" ", arguments));
            return Task.FromResult((exit, output));
        }
    }

    [Fact]
    public async Task RunsDotnetRestoreOnTheSolution()
    {
        var runner = new FakeRunner(0, "Restored.");

        var result = await new PackageRestorer(runner).RestoreAsync(@"C:\repo", "App.slnx");

        Assert.Equal(["restore App.slnx"], runner.Calls);
        Assert.Equal(@"C:\repo", runner.WorkingDirectory);
        Assert.True(result.Ok);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task AFailedRestore_IsReportedWithItsExitCodeAndOutput()
    {
        var result = await new PackageRestorer(new FakeRunner(1, "error NU1101: Unable to find package X")).RestoreAsync(@"C:\repo", "App.slnx");

        Assert.False(result.Ok);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("NU1101", result.Output);
    }

    [Fact]
    public async Task LongOutput_IsCutToTheTail()
    {
        var output = string.Join('\n', Enumerable.Range(1, 500).Select(i => "line " + i));

        var result = await new PackageRestorer(new FakeRunner(0, output)).RestoreAsync(@"C:\repo", "App.slnx");

        Assert.Contains("line 500", result.Output);
        Assert.DoesNotContain("line 1\n", result.Output);
        Assert.True(result.Output.Split('\n').Length <= PackageRestorer.MaxLines + 1);
    }
}
