using Xunit;
using AiDevHelpers.Common;

namespace AiDevHelpers.Common.Tests;

public sealed class WorkspacePathGuardTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "fe-guard-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _outside;

    public WorkspacePathGuardTests()
    {
        _root = Path.Combine(_base, "ws");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose() => Directory.Delete(_base, true);

    [Fact]
    public void Relative_path_inside_root_passes()
    {
        var r = WorkspacePathGuard.Resolve("src/a.cs", _root);
        Assert.True(r.Ok);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "src", "a.cs")), r.FullPath);
    }

    [Fact]
    public void Dotdot_escape_fails()
    {
        var r = WorkspacePathGuard.Resolve("../outside/x.cs", _root);
        Assert.False(r.Ok);
        Assert.Contains("outside the workspace root", r.Error);
    }

    [Fact]
    public void Absolute_path_outside_root_fails()
    {
        var r = WorkspacePathGuard.Resolve(Path.Combine(_outside, "x.cs"), _root);
        Assert.False(r.Ok);
    }

    [Fact]
    public void Absolute_path_inside_root_passes()
    {
        Assert.True(WorkspacePathGuard.Resolve(Path.Combine(_root, "x.cs"), _root).Ok);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Missing_root_fails_without_cwd_fallback(string? root)
    {
        var r = WorkspacePathGuard.Resolve("a.cs", root);
        Assert.False(r.Ok);
        Assert.Contains("No workspace root", r.Error);
    }

    [Fact]
    public void Empty_path_fails()
    {
        var r = WorkspacePathGuard.Resolve(" ", _root);
        Assert.False(r.Ok);
        Assert.Contains("path is required", r.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extra_root_is_allowed()
    {
        Assert.True(WorkspacePathGuard.Resolve(Path.Combine(_outside, "b.bak"), _root, _outside).Ok);
    }

    [Fact]
    public void Directory_link_escape_fails()
    {
        var link = Path.Combine(_root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, _outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return; // symlink creation needs a privilege; not available on this machine
        }

        var r = WorkspacePathGuard.Resolve("link/x.cs", _root);
        Assert.False(r.Ok);
    }
}
