using System.Text.Json.Nodes;
using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class InstanceCheckTests
{
    private const string Sln = @"F:\repo\App.sln";

    private static string List(params string[] instances) =>
        "{\"count\":" + instances.Length + ",\"instances\":[" + string.Join(",", instances) + "],\"starting\":[]}";

    private static string Instance(int pid, string solution, string? branch, string? worktree) =>
        "{\"pid\":" + pid + ",\"solution_path\":\"" + solution.Replace("\\", "\\\\") + "\",\"git_branch\":"
        + (branch is null ? "null" : "\"" + branch + "\"") + ",\"git_worktree\":"
        + (worktree is null ? "null" : "\"" + worktree.Replace("\\", "\\\\") + "\"") + "}";

    [Fact]
    public void NoSolutionPath_AddsNoCheck()
    {
        var json = InstanceCheck.Apply(List(Instance(1, Sln, "wip", @"F:\repo")), null, null, null);

        Assert.Null(JsonNode.Parse(json)!["check"]);
    }

    [Fact]
    public void SolutionOpenOnExpectedBranchAndWorktree_IsOk()
    {
        var json = InstanceCheck.Apply(List(Instance(7, Sln, "wip", @"F:\repo")), Sln, "wip", @"F:\repo");
        var check = JsonNode.Parse(json)!["check"]!;

        Assert.True(check["solution_open"]!.GetValue<bool>());
        Assert.True(check["ok"]!.GetValue<bool>());
        Assert.Equal(7, check["matching_instance_pids"]![0]!.GetValue<int>());
    }

    [Fact]
    public void SolutionOpenOnAnotherBranch_IsNotOk()
    {
        var json = InstanceCheck.Apply(List(Instance(7, Sln, "main", @"F:\repo")), Sln, "wip", @"F:\repo");
        var check = JsonNode.Parse(json)!["check"]!;

        Assert.True(check["solution_open"]!.GetValue<bool>());
        Assert.False(check["ok"]!.GetValue<bool>());
        Assert.Empty(check["matching_instance_pids"]!.AsArray());
    }

    [Fact]
    public void SolutionOpenInAnotherWorktree_IsNotOk()
    {
        var json = InstanceCheck.Apply(List(Instance(7, Sln, "wip", @"F:\other")), Sln, "wip", @"F:\repo");
        var check = JsonNode.Parse(json)!["check"]!;

        Assert.False(check["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void SolutionNotOpen_ReportsItAndIsNotOk()
    {
        var json = InstanceCheck.Apply(List(Instance(7, @"F:\other\Other.sln", "wip", @"F:\other")), Sln, "wip", @"F:\repo");
        var check = JsonNode.Parse(json)!["check"]!;

        Assert.False(check["solution_open"]!.GetValue<bool>());
        Assert.False(check["ok"]!.GetValue<bool>());
    }
}
