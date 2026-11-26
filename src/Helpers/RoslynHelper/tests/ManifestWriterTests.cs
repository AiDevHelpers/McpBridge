using AiDevHelpers.Helpers.RoslynHelper;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

public class ManifestWriterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "roslyn-manifest-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void Write_then_Read_round_trips_pid_pipeName_and_solutionPath()
    {
        var pid = Environment.ProcessId;
        const string pipe = "AiDevHelpers.Roslyn.test";
        const string solution = @"C:\repo\App.slnx";

        ManifestWriter.Write(_folder, pid, pipe, solution);
        var manifest = ManifestWriter.Read(_folder, pid);

        Assert.NotNull(manifest);
        Assert.Equal(pid, manifest!.Pid);
        Assert.Equal(pipe, manifest.PipeName);
        Assert.Equal(solution, manifest.SolutionPath);
    }

    [Fact]
    public void Remove_deletes_the_manifest_file()
    {
        var pid = Environment.ProcessId;
        ManifestWriter.Write(_folder, pid, "AiDevHelpers.Roslyn.test", @"C:\repo\App.slnx");
        var file = Path.Combine(_folder, $"{pid}.json");
        Assert.True(File.Exists(file));

        ManifestWriter.Remove(_folder, pid);

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Read_returns_null_when_the_pid_is_not_running()
    {
        const int deadPid = 2147483000;
        Directory.CreateDirectory(_folder);
        File.WriteAllText(
            Path.Combine(_folder, $"{deadPid}.json"),
            $"{{\"pid\":{deadPid},\"pipeName\":\"AiDevHelpers.Roslyn.{deadPid}\",\"solutionPath\":\"C:\\\\repo\\\\App.slnx\"}}");

        Assert.Null(ManifestWriter.Read(_folder, deadPid));
    }
}
