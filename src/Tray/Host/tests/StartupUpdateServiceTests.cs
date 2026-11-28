using AiDevHelpers.Host;
using AiDevHelpers.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiDevHelpers.Host.Tests;

public sealed class StartupUpdateServiceTests
{
    private sealed class FakeInstaller(Func<StagedInstallResult> result) : IStagedUpdateInstaller
    {
        public List<string> Events { get; } = [];

        public Task<StagedInstallResult> InstallNewestAsync(string currentVersion, bool allowDevPackages, CancellationToken ct)
        {
            Events.Add("install " + currentVersion + " allowDev=" + allowDevPackages);
            return Task.FromResult(result());
        }
    }

    // The install folder's ACL is set before anything is installed into the updates folder.
    [Fact]
    public async Task StartupLocksFolderThenInstallsBeforeReturn()
    {
        var installer = new FakeInstaller(() => new StagedInstallResult(StagedInstallOutcome.Applied, "done", "2.0.0"));
        var events = new List<string>();
        var service = new StartupUpdateService(installer, () => "1.0.0", "C:\\ProgramData\\AiDevHelpers\\updates", folder => events.Add("lock " + folder), NullLogger<StartupUpdateService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(["lock C:\\ProgramData\\AiDevHelpers\\updates"], events);
        Assert.Equal(["install 1.0.0 allowDev=False"], installer.Events);
    }

    // A failed startup install is logged, never fatal: the Host still starts and supervises MCP.Http.
    [Fact]
    public async Task StartupFailureDoesNotStopHost()
    {
        var installer = new FakeInstaller(() => throw new IOException("disk full"));
        var service = new StartupUpdateService(installer, () => "1.0.0", "x", _ => { }, NullLogger<StartupUpdateService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.Single(installer.Events);
    }
}
