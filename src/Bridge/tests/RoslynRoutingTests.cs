using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AiDevHelpers.Bridge;
using Xunit;

namespace AiDevHelpers.Bridge.Tests;

// Routing of edit actions to a Roslyn helper for a VS Code solution, by the framework of the project each action touches.
// The decision, the classification and the manifest pick are pure, so no pipe or process is needed.
public class RoslynRoutingTests
{
    private static readonly RoslynHelperManifest Net10 = new(1, "AiDevHelpers.Roslyn.1", @"F:\repo\App.slnx", "net10");
    private static readonly RoslynHelperManifest Net48 = new(2, "AiDevHelpers.Roslyn48.2", @"F:\repo\App.slnx", "net48");
    private static readonly RoslynHelperManifest DeadNet48 = new(3, "AiDevHelpers.Roslyn48.3", @"F:\repo\App.slnx", "net48");

    private const string SdkNet10 = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
    private const string SdkNet48 = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>";
    private const string Legacy = "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion></PropertyGroup></Project>";

    [Fact]
    public void pick_returns_the_live_helper_of_the_framework_and_skips_a_dead_one()
    {
        var picked = RoslynHelperRegistry.Pick(new[] { DeadNet48, Net10, Net48 }, @"f:\REPO\app.slnx", "net48", pid => pid != 3);

        Assert.Equal(Net48, picked);
    }

    [Fact]
    public void pick_never_returns_a_helper_of_the_other_framework()
    {
        Assert.Null(RoslynHelperRegistry.Pick(new[] { Net10 }, @"F:\repo\App.slnx", "net48", pid => true));
        Assert.Null(RoslynHelperRegistry.Pick(new[] { DeadNet48 }, @"F:\repo\App.slnx", "net48", pid => false));
    }

    [Fact]
    public void a_manifest_without_a_framework_is_a_net10_helper()
    {
        var manifest = JsonSerializer.Deserialize<RoslynHelperManifest>("{\"pid\":4,\"pipeName\":\"AiDevHelpers.Roslyn.4\",\"solutionPath\":\"F:\\\\repo\\\\App.slnx\"}");

        Assert.Equal("net10", manifest!.Framework);
    }

    [Fact]
    public void an_edit_on_a_net48_project_routes_to_the_net48_helper()
    {
        var dir = Path.Combine(Path.GetTempPath(), "route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Legacy"));
        File.WriteAllText(Path.Combine(dir, "Legacy", "Legacy.csproj"), Legacy);
        var file = Path.Combine(dir, "Legacy", "Class1.cs");

        var route = RoslynRouting.Decide("editMethod", "VsCode", @"F:\repo\App.slnx", file,
            (solution, framework) => framework == "net48" ? Net48 : null);

        Assert.Equal(RoslynRouteKind.Helper, route.Kind);
        Assert.Equal(Net48, route.Helper);
    }

    [Fact]
    public void an_edit_on_an_sdk_net10_project_routes_to_the_net10_helper()
    {
        var dir = Path.Combine(Path.GetTempPath(), "route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "App.csproj"), SdkNet10);

        var route = RoslynRouting.Decide("editMethod", "VsCode", @"F:\repo\App.slnx", Path.Combine(dir, "Class1.cs"),
            (solution, framework) => framework == "net10" ? Net10 : null);

        Assert.Equal(RoslynRouteKind.Helper, route.Kind);
        Assert.Equal(Net10, route.Helper);
    }

    [Fact]
    public void an_edit_on_a_net48_project_with_no_net48_helper_is_refused_and_names_the_framework()
    {
        var dir = Path.Combine(Path.GetTempPath(), "route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Legacy.csproj"), SdkNet48);
        var refused = RoslynRouting.Decide("editMethod", "VsCode", @"F:\repo\App.slnx", Path.Combine(dir, "Class1.cs"),
            (solution, framework) => framework == "net10" ? Net10 : null);

        Assert.Equal(RoslynRouteKind.NoHelper, refused.Kind);
        Assert.Contains("net48", refused.Error);
    }

    [Fact]
    public void a_non_edit_action_and_a_visual_studio_solution_stay_on_the_existing_bridge()
    {
        Assert.Equal(RoslynRouteKind.Bridge, RoslynRouting.Decide("build", "VsCode", @"F:\repo\App.slnx", null, (s, f) => Net10).Kind);
        Assert.Equal(RoslynRouteKind.Bridge, RoslynRouting.Decide("editMethod", "VisualStudio", @"F:\repo\App.slnx", null, (s, f) => Net10).Kind);
        Assert.Equal(RoslynRouteKind.Bridge, RoslynRouting.Decide("editMethod", null, @"F:\repo\App.slnx", null, (s, f) => Net10).Kind);
    }

    [Fact]
    public void classify_reads_sdk_net10_sdk_net48_and_legacy_projects()
    {
        Assert.Equal("net10", RoslynRouting.Classify(SdkNet10));
        Assert.Equal("net48", RoslynRouting.Classify(SdkNet48));
        Assert.Equal("net48", RoslynRouting.Classify(Legacy));
    }

    [Fact]
    public void the_routed_action_set_matches_the_shared_edit_actions()
    {
        // conformance/roslyn-actions.json is the list the helpers, the Bridge and the VS Code client all match.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "conformance", "roslyn-actions.json")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "conformance", "roslyn-actions.json")));
        var shared = json.RootElement.GetProperty("editActions").EnumerateArray().Select(e => e.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.Equal(shared, RoslynRouting.Actions.OrderBy(x => x, StringComparer.Ordinal).ToList());
    }
}
