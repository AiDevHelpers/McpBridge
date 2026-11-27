using AiDevHelpers.Common;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class InstallWorkloadFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sh-workload-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>The table block as the script embeds it: the lines between the here-string markers.</summary>
    private static string EmbeddedTable(string script)
    {
        var lines = script.Split('\n');
        var start = Array.FindIndex(lines, l => l == "$table = @'");
        var end = Array.FindIndex(lines, start + 1, l => l == "'@");
        return string.Join("\n", lines[(start + 1)..end]);
    }

    [Fact]
    public void ScriptPinsTheTableHash()
    {
        Assert.Contains($"$pin = '{InstallableWorkloads.TableHash()}'", InstallWorkloadFiles.Script());
    }

    [Fact]
    public void TableInTheScriptMatchesTheTable()
    {
        var table = EmbeddedTable(InstallWorkloadFiles.Script());

        Assert.Equal(InstallableWorkloads.TableText().TrimEnd('\n'), table);
    }

    [Fact]
    public void ATamperedTableDoesNotMatchThePin()
    {
        var tampered = EmbeddedTable(InstallWorkloadFiles.Script()).Replace("maui", "evil-workload", StringComparison.Ordinal);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tampered + "\n")));

        Assert.NotEqual(InstallableWorkloads.TableHash(), hash);
    }

    [Fact]
    public void TheScriptTakesOnlyAWorkloadNameNeverAnId()
    {
        var script = InstallWorkloadFiles.Script();

        Assert.Contains("param([Parameter(Mandatory)][string]$Workload)", script);
        Assert.Contains("dotnet.exe workload install $Workload", script);
        Assert.DoesNotContain("Microsoft.VisualStudio", script);
    }

    [Fact]
    public void WriteCreatesTheScriptInTheToolsFolder()
    {
        InstallWorkloadFiles.Write(_root);

        Assert.Equal(InstallWorkloadFiles.Script(), File.ReadAllText(Path.Combine(_root, InstallWorkloadFiles.ScriptName)));
    }
}
