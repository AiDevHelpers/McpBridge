using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class ElevationScannerTests : IDisposable
{
    private const string AdminManifest = """
        <assembly><trustInfo><security><requestedPrivileges>
          <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
        </requestedPrivileges></security></trustInfo></assembly>
        """;

    private const string NormalManifest = """
        <assembly><trustInfo><security><requestedPrivileges>
          <requestedExecutionLevel level="asInvoker" uiAccess="false" />
        </requestedPrivileges></security></trustInfo></assembly>
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sh-elev-" + Guid.NewGuid());

    public ElevationScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private IReadOnlyList<ElevationFinding> Scan() => ElevationScanner.Scan(_root);

    [Fact]
    public void AProjectWithARequireAdministratorManifest_IsFound_ThroughASlnx()
    {
        Write("App.slnx", """<Solution><Project Path="src/Drv/Drv.csproj" /><Project Path="src/Ok/Ok.csproj" /></Solution>""");
        Write("src/Drv/Drv.csproj", "<Project />");
        Write("src/Drv/app.manifest", AdminManifest);
        Write("src/Ok/Ok.csproj", "<Project />");
        Write("src/Ok/app.manifest", NormalManifest);

        var finding = Assert.Single(Scan());

        Assert.Equal("src/Drv/Drv.csproj", finding.Project);
        Assert.Equal(ElevationKind.ProjectManifest, finding.Kind);
        Assert.Contains("requireAdministrator", finding.Evidence);
    }

    [Fact]
    public void AProjectIsFound_ThroughAnSlnFile()
    {
        Write("App.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Drv", "src\Drv\Drv.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            """);
        Write("src/Drv/Drv.csproj", "<Project />");
        Write("src/Drv/app.manifest", AdminManifest);

        Assert.Equal("src/Drv/Drv.csproj", Assert.Single(Scan()).Project);
    }

    [Fact]
    public void WithoutASolution_EveryCsprojIsLookedAt()
    {
        Write("src/Drv/Drv.csproj", "<Project />");
        Write("src/Drv/app.manifest", AdminManifest);

        Assert.Equal("src/Drv/Drv.csproj", Assert.Single(Scan()).Project);
    }

    [Fact]
    public void TheManifestNamedInTheProjectFile_IsUsed()
    {
        Write("src/Drv/Drv.csproj", "<Project><PropertyGroup><ApplicationManifest>Props\\admin.manifest</ApplicationManifest></PropertyGroup></Project>");
        Write("src/Drv/Props/admin.manifest", AdminManifest);

        Assert.Equal("src/Drv/Drv.csproj", Assert.Single(Scan()).Project);
    }

    [Fact]
    public void AScriptWithAMarker_RunByALaunchProfile_IsReportedAgainstThatProject()
    {
        Write("src/App/App.csproj", "<Project />");
        Write("src/App/Properties/launchSettings.json", """
            { "profiles": { "dbg": { "commandName": "Executable", "executablePath": "pwsh", "commandLineArgs": "-File ../../tools/run.ps1" } } }
            """);
        Write("tools/run.ps1", "Write-Host hi\n#Requires -RunAsAdministrator\n");

        var finding = Assert.Single(Scan());

        Assert.Equal("src/App/App.csproj", finding.Project);
        Assert.Equal(ElevationKind.DebugScript, finding.Kind);
        Assert.Equal("tools/run.ps1", finding.File);
    }

    [Fact]
    public void AScriptWithAMarker_RunByAVsCodeConfiguration_IsReportedWithoutAProject()
    {
        Write(".vscode/launch.json", """{ "configurations": [ { "name": "go", "program": "tools/run.cmd" } ] }""");
        Write("tools/run.cmd", "@echo off\nnet session >nul 2>&1\n");

        var finding = Assert.Single(Scan());

        Assert.Null(finding.Project);
        Assert.Equal(ElevationKind.ScriptMarker, finding.Kind);
        Assert.Equal("tools/run.cmd", finding.File);
    }

    [Theory]
    [InlineData("a.ps1", "Start-Process pwsh -Verb RunAs -ArgumentList x")]
    [InlineData("a.ps1", "if (([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { }")]
    [InlineData("a.cmd", "fltmc >nul 2>&1")]
    [InlineData("a.bat", "powershell -Command \"Start-Process cmd -Verb RunAs\"")]
    public void EachMarker_IsRecognised(string name, string line)
    {
        Write(".vscode/launch.json", "{ \"configurations\": [ { \"name\": \"n\", \"program\": \"" + name + "\" } ] }");
        Write(name, line + "\n");

        Assert.Single(Scan());
    }

    [Theory]
    [InlineData("a.ps1", "# Start-Process pwsh -Verb RunAs")]
    [InlineData("a.ps1", "Write-Host 'no marker here'")]
    [InlineData("a.cmd", "REM net session")]
    [InlineData("a.cmd", ":: fltmc")]
    public void CommentsAndPlainScripts_AreNotHits(string name, string line)
    {
        Write(".vscode/launch.json", "{ \"configurations\": [ { \"name\": \"n\", \"program\": \"" + name + "\" } ] }");
        Write(name, line + "\n");

        Assert.Empty(Scan());
    }

    [Fact]
    public void ScriptsNoLaunchConfigurationRuns_AreNotRead()
    {
        Write("tools/other.ps1", "#Requires -RunAsAdministrator\n");

        Assert.Empty(Scan());
    }

    [Fact]
    public void UnreadableProjectAndManifestFiles_AreSkipped()
    {
        Write("App.slnx", "<Solution><not-closed");
        Write("src/Drv/Drv.csproj", "<Project");
        Write("src/Drv/app.manifest", "not xml at all");

        Assert.Empty(Scan());
    }
}
