using AiDevHelpers.Common;
using Xunit;

namespace AiDevHelpers.Common.Tests;

public sealed class InstallableWorkloadsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AiDevHelpers.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("AiDevHelpers.slnx not found above the test output folder.");
    }

    [Fact]
    public void TableHashMatchesTheCommittedPin()
    {
        var pinFile = Path.Combine(RepoRoot(), "scripts", "workload-install.sha256");

        Assert.True(File.Exists(pinFile), $"No pin file. Commit {pinFile} with the value {InstallableWorkloads.TableHash()}.");
        Assert.Equal(InstallableWorkloads.TableHash(), File.ReadAllText(pinFile).Trim());
    }

    [Fact]
    public void LookupIsCaseInsensitiveAndExact()
    {
        Assert.Equal("maui", InstallableWorkloads.TryGet("MAUI")?.Name);
        Assert.Null(InstallableWorkloads.TryGet("maui; rm -rf"));
        Assert.Null(InstallableWorkloads.TryGet("Microsoft.VisualStudio.Workload.ManagedDesktop"));
    }

    [Fact]
    public void TableTextIsOneNamePerLineWithLfEndings()
    {
        var text = InstallableWorkloads.TableText();

        Assert.DoesNotContain('\r', text);
        Assert.Equal(InstallableWorkloads.All.Count, text.TrimEnd('\n').Split('\n').Length);
    }
}
