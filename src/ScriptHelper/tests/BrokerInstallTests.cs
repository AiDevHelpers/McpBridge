using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class BrokerInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sh-install-" + Guid.NewGuid());
    private readonly string _install;

    public BrokerInstallTests()
    {
        _install = Path.Combine(_root, "bin");
        Directory.CreateDirectory(_install);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void An_exe_outside_the_install_folder_is_not_trusted()
    {
        var elsewhere = Path.Combine(_root, "downloads", "scripthelper-broker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        File.WriteAllText(elsewhere, "x");

        Assert.False(BrokerInstall.IsTrusted(elsewhere, _install, _root));
    }

    [Fact]
    public void An_exe_in_a_folder_that_ordinary_users_can_write_to_is_not_trusted()
    {
        var exe = Path.Combine(_install, "scripthelper-broker.exe");
        File.WriteAllText(exe, "x");

        Assert.False(AdminOnlyDirectory.IsAdminOnly(_install));
        Assert.False(BrokerInstall.IsTrusted(exe, _install, _root));
    }
}
