using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class ToolsetSessionLockTests
{
    private const string Repo = @"C:\repos\app";

    [Fact]
    public void LockRecordsTheRepoAndThePath()
    {
        var locks = new ToolsetSessionLock();

        var entry = locks.Lock("chat-a", Repo, () => ToolsetPaths.VsCode);

        Assert.Equal(Repo, entry.RepoRoot);
        Assert.Equal(ToolsetPaths.VsCode, entry.Path);
        Assert.Same(entry, locks.Find("chat-a"));
    }

    [Fact]
    public void SameSessionKeepsItsFirstLock()
    {
        var locks = new ToolsetSessionLock();

        locks.Lock("chat-a", Repo, () => ToolsetPaths.VsCode);
        var later = locks.Lock("chat-a", @"C:\repos\other", () => ToolsetPaths.Standalone);

        Assert.Equal(Repo, later.RepoRoot);
        Assert.Equal(ToolsetPaths.VsCode, later.Path);
    }

    [Fact]
    public void ResolverRunsOnlyOncePerSession()
    {
        var locks = new ToolsetSessionLock();
        var calls = 0;

        locks.Lock("chat-a", Repo, () => { calls++; return ToolsetPaths.Standalone; });
        locks.Lock("chat-a", Repo, () => { calls++; return ToolsetPaths.Standalone; });

        Assert.Equal(1, calls);
    }

    [Fact]
    public void FindIsNullBeforeAnyLock()
    {
        var locks = new ToolsetSessionLock();

        Assert.Null(locks.Find("chat-a"));
    }

    [Fact]
    public void DifferentSessionsGetTheirOwnLock()
    {
        var locks = new ToolsetSessionLock();

        var a = locks.Lock("chat-a", Repo, () => ToolsetPaths.VsCode);
        var b = locks.Lock("chat-b", @"C:\repos\other", () => ToolsetPaths.Standalone);

        Assert.Equal(ToolsetPaths.VsCode, a.Path);
        Assert.Equal(ToolsetPaths.Standalone, b.Path);
    }

    [Fact]
    public void MissingSessionIdsShareOneProcessKey()
    {
        var locks = new ToolsetSessionLock();

        var fromNull = locks.Lock(null, Repo, () => ToolsetPaths.VisualStudio);
        var fromEmpty = locks.Lock(string.Empty, @"C:\repos\other", () => ToolsetPaths.Standalone);

        Assert.Equal(ToolsetPaths.VisualStudio, fromEmpty.Path);
        Assert.Same(fromNull, locks.Find(null));
    }

    [Fact]
    public void RepoRootIsTheNearestFolderWithAGitMarker()
    {
        var top = Path.Combine(Path.GetTempPath(), "toolset-disc-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(top, "src", "App");
        Directory.CreateDirectory(Path.Combine(top, ".git"));
        Directory.CreateDirectory(nested);
        try
        {
            Assert.Equal(top, ToolsetRepoDiscovery.RepoRootFor(null, nested));
            Assert.Equal(top, ToolsetRepoDiscovery.RepoRootFor(Path.Combine(top, "src"), Path.GetTempPath()));
        }
        finally
        {
            Directory.Delete(top, recursive: true);
        }
    }

    [Fact]
    public void LockForSessionLocksOnceARepoIsFound()
    {
        var top = Path.Combine(Path.GetTempPath(), "toolset-lockfor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(top, ".git"));
        try
        {
            var locks = new ToolsetSessionLock();
            var options = new AiDevHelpers.MCP.Core.Services.IdeToolsOptions { ToolsetPath = ToolsetPaths.VsCode };

            var locked = ToolsetRepoDiscovery.LockFor(locks, "chat-a", options, null, top);

            Assert.NotNull(locked);
            Assert.Equal(top, locked!.RepoRoot);
            Assert.Equal(ToolsetPaths.VsCode, locked.Path);
            Assert.Same(locked, locks.Find("chat-a"));
        }
        finally
        {
            Directory.Delete(top, recursive: true);
        }
    }

    [Fact]
    public void LockForSessionStaysUnlockedOutsideAnyRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "toolset-lockfor-none-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var locks = new ToolsetSessionLock();

            var locked = ToolsetRepoDiscovery.LockFor(locks, "chat-a", new AiDevHelpers.MCP.Core.Services.IdeToolsOptions(), null, dir);

            Assert.Null(locked);
            Assert.Null(locks.Find("chat-a"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NoRepoRootWhenNoGitMarkerExists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "toolset-norepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(ToolsetRepoDiscovery.RepoRootFor(null, dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
