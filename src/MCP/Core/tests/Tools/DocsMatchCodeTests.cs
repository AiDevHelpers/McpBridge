using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

/// <summary>Phase 8 gate: the docs and fixtures list every MCP tool and every bridge action that the code has.</summary>
public sealed partial class DocsMatchCodeTests
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

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    private static string Docs() => Read("docs", "ide-bridge.md") + "\n" + Read("conformance", "protocol.md");

    private static string SnakeCase(string name) => Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();

    [Fact]
    public void EveryMcpToolIsListedInTheDocs()
    {
        var docs = Docs();
        var core = typeof(SelfTestCoverageTests).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name).Contains("AiDevHelpers.MCP.Core")
            ? Assembly.Load("AiDevHelpers.MCP.Core")
            : throw new InvalidOperationException("MCP Core is not referenced.");

        var missing = new List<string>();
        foreach (var type in core.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null))
            {
                // The docs name a tool by its snake_case MCP name or its C# method name.
                if (!docs.Contains($"`{SnakeCase(method.Name)}`") && !docs.Contains($"`{method.Name}`"))
                {
                    missing.Add($"{type.Name}.{method.Name} (`{SnakeCase(method.Name)}`)");
                }
            }
        }

        Assert.True(missing.Count == 0, "Add these tools to docs/ide-bridge.md:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void EveryBridgeActionIsListedInTheDocs()
    {
        var docs = Docs();
        var csharp = Read("src", "Installers", "Vs2026", "Core", "src", "IdeActions.cs");
        var typescript = Read("src", "Installers", "VsCode", "Installer", "AiDevTools.VsCode", "src", "ideActions.ts");

        var actions = CSharpAction().Matches(csharp).Select(m => m.Groups["name"].Value)
            .Concat(TypeScriptAction().Matches(typescript).Select(m => m.Groups["name"].Value))
            .Where(a => a != "health")
            .Distinct()
            .OrderBy(a => a)
            .ToList();

        Assert.NotEmpty(actions);
        var missing = actions.Where(a => !docs.Contains($"`{a}`")).ToList();
        Assert.True(missing.Count == 0, "Add these actions to docs/ide-bridge.md or conformance/protocol.md:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void EveryHelperProjectIsInTheSolution()
    {
        var solution = Read("AiDevHelpers.slnx");
        var projects = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .Where(f => !f.Contains("/node_modules/"))
            // Test fixtures are sample inputs for tests, kept in their own Fixtures.slnx; they are not solution members.
            .Where(f => !f.Contains("/tests/Fixtures/"))
            .ToList();

        var missing = projects.Where(p => !solution.Contains(p)).ToList();
        Assert.True(missing.Count == 0, "Add these projects to AiDevHelpers.slnx:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void EveryHelperIsPackedByTheTagBuild()
    {
        var ci = Read(".github", "workflows", "ci.yml");
        var helpers = Directory.EnumerateDirectories(Path.Combine(RepoRoot(), "src", "Helpers"))
            .Select(Path.GetFileName)
            .Where(h => h is not null && File.Exists(Path.Combine(RepoRoot(), "src", "Helpers", h, "src", $"AiDevHelpers.Helpers.{h}.csproj")))
            .Where(h => File.ReadAllText(Path.Combine(RepoRoot(), "src", "Helpers", h!, "src", $"AiDevHelpers.Helpers.{h}.csproj")).Contains("<IsPackable>true</IsPackable>"))
            .ToList();

        var missing = helpers.Where(h => !ci.Contains($"src/Helpers/{h}/src/AiDevHelpers.Helpers.{h}.csproj")).ToList();
        Assert.True(missing.Count == 0, "Add these helpers to the Pack step in ci.yml:\n" + string.Join("\n", missing));
    }

    [GeneratedRegex(@"^\s*""(?<name>[A-Za-z]+)""\s*=>", RegexOptions.Multiline)]
    private static partial Regex CSharpAction();

    [GeneratedRegex(@"^\s*case ""(?<name>[A-Za-z]+)""", RegexOptions.Multiline)]
    private static partial Regex TypeScriptAction();
}
