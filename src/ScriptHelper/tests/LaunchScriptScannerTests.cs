using System.Text.Json.Nodes;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class LaunchScriptScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sh-scan-" + Guid.NewGuid());

    public LaunchScriptScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private IReadOnlyList<FoundScript> Scan(params AllowlistEntry[] listed) => LaunchScriptScanner.Scan(_root, listed);

    [Fact]
    public void LaunchSettings_ExecutableAndArgsThatNameScripts_AreFound()
    {
        Write("src/App/Properties/launchSettings.json", """
            { "profiles": { "dbg": { "commandName": "Executable", "executablePath": "pwsh", "commandLineArgs": "-File ../../scripts/reset.ps1 -Force" } } }
            """);
        Write("scripts/reset.ps1", "x");

        var found = Assert.Single(Scan());

        Assert.Equal("scripts/reset.ps1", found.Path);
        Assert.Contains("launchSettings", found.Source);
    }

    [Fact]
    public void VsCodeLaunch_ProgramArgsAndWorkspaceFolderVariables_AreFound()
    {
        Write(".vscode/launch.json", """
            // comments and trailing commas are allowed in launch.json
            { "configurations": [ { "name": "x", "program": "${workspaceFolder}/tools/start.cmd", "args": ["--flag", "tools/other.bat",], } ] }
            """);
        Write("tools/start.cmd", "x");
        Write("tools/other.bat", "x");

        var paths = Scan().Select(f => f.Path).Order().ToList();

        Assert.Equal(["tools/other.bat", "tools/start.cmd"], paths);
    }

    [Fact]
    public void VsCodeTasks_CommandAndArgs_AreFound()
    {
        Write(".vscode/tasks.json", """
            { "tasks": [ { "label": "t", "command": "./build/run.sh", "args": ["a"] } ] }
            """);
        Write("build/run.sh", "x");

        Assert.Equal("build/run.sh", Assert.Single(Scan()).Path);
    }

    [Fact]
    public void ScriptsThatDoNotExist_OrLeaveTheRepo_AreIgnored()
    {
        Write(".vscode/launch.json", """
            { "configurations": [ { "program": "missing/none.ps1" }, { "program": "../outside.ps1" }, { "program": "C:/Windows/x.cmd" } ] }
            """);

        Assert.Empty(Scan());
    }

    [Fact]
    public void ScriptsAlreadyInTheAllowlist_AreMarkedListed()
    {
        Write(".vscode/launch.json", """{ "configurations": [ { "program": "scripts/a.ps1" }, { "program": "scripts/b.ps1" } ] }""");
        Write("scripts/a.ps1", "x");
        Write("scripts/b.ps1", "x");

        var found = Scan(new AllowlistEntry("scripts/a.ps1", null)).ToDictionary(f => f.Path, f => f.Listed);

        Assert.True(found["scripts/a.ps1"]);
        Assert.False(found["scripts/b.ps1"]);
    }

    [Fact]
    public void AScriptNamedTwice_IsReportedOnce()
    {
        Write(".vscode/launch.json", """{ "configurations": [ { "program": "scripts/a.ps1", "args": ["scripts/a.ps1"] } ] }""");
        Write("scripts/a.ps1", "x");

        Assert.Single(Scan());
    }

    [Fact]
    public void UnreadableConfigFiles_AreSkipped()
    {
        Write(".vscode/launch.json", "{ not json");
        Write(".vscode/tasks.json", """{ "tasks": [ { "command": "run.ps1" } ] }""");
        Write("run.ps1", "x");

        Assert.Equal("run.ps1", Assert.Single(Scan()).Path);
    }

    [Fact]
    public void FilesInBuildAndDependencyFolders_AreNotScanned()
    {
        Write("node_modules/pkg/.vscode/launch.json", """{ "configurations": [ { "program": "x.ps1" } ] }""");
        Write("node_modules/pkg/.vscode/x.ps1", "x");
        Write("bin/Debug/Properties/launchSettings.json", """{ "profiles": { "p": { "commandLineArgs": "y.ps1" } } }""");

        Assert.Empty(Scan());
    }

    [Fact]
    public void AddScript_AppendsAnEntryAndKeepsTheRest()
    {
        var updated = ManifestEditor.AddScript("""{ "scripts": [ { "path": "scripts/a.ps1", "args": ["x"] } ] }""", "tools/b.cmd");

        var scripts = JsonNode.Parse(updated)!["scripts"]!.AsArray();
        Assert.Equal(2, scripts.Count);
        Assert.Equal("scripts/a.ps1", scripts[0]!["path"]!.GetValue<string>());
        Assert.Equal("x", scripts[0]!["args"]![0]!.GetValue<string>());
        Assert.Equal("tools/b.cmd", scripts[1]!["path"]!.GetValue<string>());
        Assert.True(ManifestParser.Parse(updated).Ok);
    }

    [Fact]
    public void AddScript_DoesNotDuplicateAnExistingPath()
    {
        var once = ManifestEditor.AddScript("""{ "scripts": [] }""", "a.ps1");

        var twice = ManifestEditor.AddScript(once, "a.ps1");

        Assert.Single(JsonNode.Parse(twice)!["scripts"]!.AsArray());
    }
}
