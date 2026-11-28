using AiDevHelpers.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiDevHelpers.Updates.Tests;

public sealed class UpdateApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "updapply-" + Guid.NewGuid().ToString("N"));
    private readonly string _package;
    private readonly string _install;
    private readonly string _backup;

    public UpdateApplierTests()
    {
        _package = Path.Combine(_root, "package");
        _install = Path.Combine(_root, "install");
        _backup = Path.Combine(_root, "backup");
        Directory.CreateDirectory(_package);
        Directory.CreateDirectory(_install);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static UpdateApplier NewApplier() => new(NullLogger<UpdateApplier>.Instance);

    private void PackageFile(string relative, string content) => WriteUnder(_package, relative, content);

    private void InstalledFile(string relative, string content) => WriteUnder(_install, relative, content);

    private static void WriteUnder(string root, string relative, string content)
    {
        string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_install, relative.Replace('/', Path.DirectorySeparatorChar)));

    private bool Exists(string relative) => File.Exists(Path.Combine(_install, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static UpdateManifest Manifest(params string[] paths) =>
        new("2.0.0", paths.Select(p => new UpdateManifestFile(p, "unused", 0)).ToList());

    [Fact]
    public async Task ApplyCopiesPackageFilesIntoAnEmptyInstall()
    {
        PackageFile("bin/app.exe", "new");

        var result = await NewApplier().ApplyAsync(_package, Manifest("bin/app.exe"), _install, _backup, [], CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("new", Read("bin/app.exe"));
    }

    [Fact]
    public async Task ApplyRemovesInstalledFilesThePackageNoLongerShips()
    {
        PackageFile("bin/app.exe", "new");
        InstalledFile("bin/old.dll", "stale");

        var result = await NewApplier().ApplyAsync(_package, Manifest("bin/app.exe"), _install, _backup, [], CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(Exists("bin/old.dll"));
    }

    [Fact]
    public async Task ApplyKeepsKeepListEntriesWhole()
    {
        PackageFile("bin/app.exe", "new");
        InstalledFile("Data/user.db", "keep me");
        InstalledFile("DataTools/tool.exe", "remove me");

        var result = await NewApplier().ApplyAsync(_package, Manifest("bin/app.exe"), _install, _backup, ["Data"], CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(Exists("Data/user.db"));
        Assert.False(Exists("DataTools/tool.exe"));
    }

    [Fact]
    public async Task UnsafePathStopsBeforeAnyFileIsTouched()
    {
        PackageFile("bin/app.exe", "new");
        InstalledFile("bin/app.exe", "old");

        var result = await NewApplier().ApplyAsync(_package, Manifest("bin/app.exe", "../escape.exe"), _install, _backup, [], CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("old", Read("bin/app.exe"));
    }

    [Fact]
    public async Task AFailedApplyRestoresThePreviousVersion()
    {
        PackageFile("bin/app.exe", "new");
        PackageFile("bin/lib.dll", "new lib");
        InstalledFile("bin/app.exe", "old");
        // A directory where a package file must go makes the write fail part-way.
        Directory.CreateDirectory(Path.Combine(_install, "bin", "lib.dll"));
        File.WriteAllText(Path.Combine(_install, "bin", "lib.dll", "inner.txt"), "x");

        var result = await NewApplier().ApplyAsync(_package, Manifest("bin/app.exe", "bin/lib.dll"), _install, _backup, [], CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("old", Read("bin/app.exe"));
    }

    [Fact]
    public async Task RollbackRestoresTheBackupAndRemovesAddedFiles()
    {
        InstalledFile("bin/app.exe", "old");
        PackageFile("bin/app.exe", "new");
        PackageFile("bin/added.dll", "added");
        await NewApplier().ApplyAsync(_package, Manifest("bin/app.exe", "bin/added.dll"), _install, _backup, [], CancellationToken.None);

        var result = await NewApplier().RollbackAsync(_install, _backup, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("old", Read("bin/app.exe"));
        Assert.False(Exists("bin/added.dll"));
    }

    [Fact]
    public async Task RollbackWithoutABackupReportsIt()
    {
        var result = await NewApplier().RollbackAsync(_install, _backup, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public void DeleteLeftoverFilesRemovesOldMarkers()
    {
        InstalledFile("bin/app.exe.old-abc", "left over");
        InstalledFile("bin/app.exe", "current");

        UpdateApplier.DeleteLeftoverFiles(_install);

        Assert.False(Exists("bin/app.exe.old-abc"));
        Assert.True(Exists("bin/app.exe"));
    }
}

