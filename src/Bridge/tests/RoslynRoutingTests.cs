using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AiDevHelpers.Bridge;
using Xunit;

namespace AiDevHelpers.Bridge.Tests;

// Routing of edit actions to the Roslyn helper for a VS Code solution. The decision and the manifest pick are pure, so no pipe or
// process is needed.
public class RoslynRoutingTests
{
    private static readonly RoslynHelperManifest Alive = new(1, "AiDevHelpers.Roslyn.1", @"F:\repo\App.slnx");
    private static readonly RoslynHelperManifest Other = new(2, "AiDevHelpers.Roslyn.2", @"F:\other\Lib.slnx");
    private static readonly RoslynHelperManifest Dead = new(3, "AiDevHelpers.Roslyn.3", @"F:\repo\App.slnx");

    [Fact]
    public void pick_returns_the_live_helper_for_the_solution_and_skips_a_dead_one()
    {
        var picked = RoslynHelperRegistry.Pick(new[] { Dead, Other, Alive }, @"f:\REPO\app.slnx", pid => pid != 3);

        Assert.Equal(Alive, picked);
    }

    [Fact]
    public void pick_returns_null_when_only_a_dead_helper_matches()
    {
        var picked = RoslynHelperRegistry.Pick(new[] { Dead }, @"F:\repo\App.slnx", pid => false);

        Assert.Null(picked);
    }

    [Fact]
    public void an_edit_action_for_a_vs_code_solution_routes_to_the_helper_manifest()
    {
        var route = RoslynRouting.Decide("editMethod", "VsCode", @"F:\repo\App.slnx", s => s == @"F:\repo\App.slnx" ? Alive : null);

        Assert.Equal(RoslynRouteKind.Helper, route.Kind);
        Assert.Equal(Alive, route.Helper);
    }

    [Fact]
    public void an_edit_action_for_a_vs_code_solution_with_no_helper_is_refused_with_a_clear_error()
    {
        var route = RoslynRouting.Decide("editMethod", "VsCode", @"F:\repo\App.slnx", _ => null);

        Assert.Equal(RoslynRouteKind.NoHelper, route.Kind);
        Assert.Contains("No Roslyn helper is running", route.Error);
    }

    [Fact]
    public void a_non_edit_action_and_a_visual_studio_solution_stay_on_the_existing_bridge()
    {
        Assert.Equal(RoslynRouteKind.Bridge, RoslynRouting.Decide("build", "VsCode", @"F:\repo\App.slnx", _ => Alive).Kind);
        Assert.Equal(RoslynRouteKind.Bridge, RoslynRouting.Decide("editMethod", "VisualStudio", @"F:\repo\App.slnx", _ => Alive).Kind);
        Assert.Equal(RoslynRouteKind.Bridge, RoslynRouting.Decide("editMethod", null, @"F:\repo\App.slnx", _ => Alive).Kind);
    }

    [Fact]
    public void the_routed_action_set_matches_the_shared_edit_actions()
    {
        // conformance/roslyn-actions.json is the list the helper, the Bridge and the VS Code client all match.
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
