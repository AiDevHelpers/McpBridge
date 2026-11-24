using System.Security.AccessControl;
using System.Security.Principal;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class RepoWalkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "repowalk-" + Guid.NewGuid().ToString("N"));
    private string? _lockedDir;
    private FileSystemAccessRule? _denyRule;

    public RepoWalkTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Files_SkipsAFolderTheUserCannotRead_AndKeepsWalking()
    {
        Assert.True(OperatingSystem.IsWindows());
        var rootFile = Path.Combine(_root, "a.txt");
        File.WriteAllText(rootFile, "a");
        var subDir = Directory.CreateDirectory(Path.Combine(_root, "sub"));
        var subFile = Path.Combine(subDir.FullName, "b.txt");
        File.WriteAllText(subFile, "b");

        _lockedDir = Directory.CreateDirectory(Path.Combine(_root, "locked")).FullName;
        File.WriteAllText(Path.Combine(_lockedDir, "hidden.txt"), "x");
        var info = new DirectoryInfo(_lockedDir);
        var security = info.GetAccessControl();
        _denyRule = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        security.AddAccessRule(_denyRule);
        info.SetAccessControl(security);

        var files = RepoWalk.Files(_root).Select(f => f.File).ToList();

        Assert.Equal(2, files.Count);
        Assert.Contains(rootFile, files);
        Assert.Contains(subFile, files);
    }

    public void Dispose()
    {
        if (_lockedDir is not null && _denyRule is not null)
        {
            var info = new DirectoryInfo(_lockedDir);
            var security = info.GetAccessControl();
            security.RemoveAccessRule(_denyRule);
            info.SetAccessControl(security);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
