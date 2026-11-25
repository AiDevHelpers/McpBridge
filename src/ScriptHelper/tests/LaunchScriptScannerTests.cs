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
    public void LaunchSettings_ReportsTheProfileAndTheOwningProject()
    {
        Write("src/App/App.csproj", "<Project />");
        Write("src/App/Properties/launchSettings.json", """
            { "profiles": { "dbg": { "commandName": "Executable", "executablePath": "pwsh", "commandLineArgs": "-File ../../scripts/reset.ps1" } } }
            """);
        Write("scripts/reset.ps1", "x");

        var owner = Assert.Single(Assert.Single(Scan()).Owners!);

        Assert.Equal("dbg", owner.Name);
        Assert.Equal("src/App", owner.Project);
    }

    [Fact]
    public void VsCodeConfigurationsAndTasks_ReportTheirNameAndNoProject()
    {
        Write(".vscode/launch.json", """{ "configurations": [ { "name": "go", "program": "tools/a.cmd" } ] }""");
        Write(".vscode/tasks.json", """{ "tasks": [ { "label": "build it", "command": "tools/b.cmd" } ] }""");
        Write("tools/a.cmd", "x");
        Write("tools/b.cmd", "x");

        var owners = Scan().ToDictionary(f => f.Path, f => Assert.Single(f.Owners!));

        Assert.Equal("go", owners["tools/a.cmd"].Name);
        Assert.Equal("build it", owners["tools/b.cmd"].Name);
        Assert.All(owners.Values, o => Assert.Null(o.Project));
    }

    [Fact]
    public void AScriptUsedByTwoProfiles_ListsBothOwners()
    {
        Write("src/App/Properties/launchSettings.json", """
            { "profiles": { "a": { "commandLineArgs": "../../s.ps1" }, "b": { "commandLineArgs": "../../s.ps1" } } }
            """);
        Write("s.ps1", "x");

        var found = Assert.Single(Scan());

        Assert.Equal(["a", "b"], found.Owners!.Select(o => o.Name).Order().ToList());
    }

    [Fact]
    public void PackagesAndDistFolders_AreNotScanned()
    {
        Write("packages/x/.vscode/launch.json", """{ "configurations": [ { "program": "r.ps1" } ] }""");
        Write("dist/y/.vscode/launch.json", """{ "configurations": [ { "program": "r.ps1" } ] }""");
        Write("r.ps1", "x");

        Assert.Empty(Scan());
    }

    [Fact]
    public void ConfigFilesBeyondTheDepthCap_AreNotScanned()
    {
        var deep = string.Join('/', Enumerable.Range(0, LaunchScriptScanner.MaxDepth + 2).Select(i => "d" + i));
        Write(deep + "/.vscode/launch.json", """{ "configurations": [ { "program": "x.ps1" } ] }""");
        Write("x.ps1", "x");

        Assert.Empty(Scan());
    }

    [Fact]
    public void ADirectoryLink_IsNotFollowed()
    {
        Write("real/.vscode/launch.json", """{ "configurations": [ { "name": "n", "program": "x.ps1" } ] }""");
        Write("x.ps1", "x");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "link"), Path.Combine(_root, "real"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return; // Creating a link needs developer mode or rights; the depth-cap test still covers a runaway walk.
        }

        // A followed link would report the same configuration a second time, as a second owner.
        var found = Assert.Single(Scan());

        Assert.Single(found.Owners!);
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
