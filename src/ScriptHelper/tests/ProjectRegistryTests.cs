using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

internal sealed class FakeElevation : IElevationCheck
{
    public FakeElevation(bool elevated) => Elevated = elevated;
    public bool Elevated { get; set; }
    public bool IsElevated() => Elevated;
}

internal sealed class FakeVolumes : IVolumeInfo
{
    public FakeVolumes(string? fileSystem = "NTFS") => FileSystemName = fileSystem;
    public string? FileSystemName { get; set; }
    public string? FileSystem(string path) => FileSystemName;
}

public sealed class ProjectRegistryTests : IDisposable
{
    private const string Profile = @"C:\Users\tester";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sh-reg-" + Guid.NewGuid());
    private readonly FakeElevation _elevation = new(true);
    private readonly FakeVolumes _volumes = new();

    public ProjectRegistryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string RegistryPath => Path.Combine(_dir, "data", "projects.json");

    private string NewRoot(string name)
    {
        var root = Path.Combine(_dir, name);
        Directory.CreateDirectory(root);
        return root;
    }

    private ProjectRegistry Registry() => new(RegistryPath, _elevation, _volumes, Profile);

    [Fact]
    public void Register_in_an_elevated_shell_succeeds_and_returns_an_id()
    {
        var r = Registry().Register(NewRoot("repo"));

        Assert.True(r.Ok);
        Assert.False(string.IsNullOrWhiteSpace(r.ProjectId));
    }

    [Fact]
    public void Register_refuses_when_not_elevated()
    {
        _elevation.Elevated = false;

        var r = Registry().Register(NewRoot("repo"));

        Assert.False(r.Ok);
        Assert.Contains("elevated", r.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(RegistryPath));
    }

    [Fact]
    public void Register_refuses_a_missing_directory()
    {
        var r = Registry().Register(Path.Combine(_dir, "nope"));

        Assert.False(r.Ok);
        Assert.Contains("does not exist", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_refuses_a_non_ntfs_volume()
    {
        _volumes.FileSystemName = "FAT32";

        var r = Registry().Register(NewRoot("repo"));

        Assert.False(r.Ok);
        Assert.Contains("NTFS", r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_refuses_an_unknown_volume_type()
    {
        _volumes.FileSystemName = null;

        Assert.False(Registry().Register(NewRoot("repo")).Ok);
    }

    [Fact]
    public void Register_refuses_the_user_profile_itself()
    {
        var r = Registry().Register(Profile);

        Assert.False(r.Ok);
        Assert.Contains("profile", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_refuses_a_folder_that_contains_the_user_profile()
    {
        var r = Registry().Register(@"C:\Users");

        Assert.False(r.Ok);
        Assert.Contains("profile", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_refuses_a_drive_root()
    {
        var r = Registry().Register(@"C:\");

        Assert.False(r.Ok);
        Assert.Contains("drive root", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registering_the_same_root_twice_returns_the_same_id_and_one_entry()
    {
        var root = NewRoot("repo");

        var first = Registry().Register(root);
        var second = Registry().Register(root + "\\");

        Assert.Equal(first.ProjectId, second.ProjectId);
        Assert.Single(File.ReadAllLines(RegistryPath), l => l.Contains("\"Id\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Root_comparison_is_case_insensitive()
    {
        var root = NewRoot("Repo");

        var first = Registry().Register(root);
        var second = Registry().Register(root.ToUpperInvariant());

        Assert.Equal(first.ProjectId, second.ProjectId);
    }

    [Fact]
    public void Different_roots_get_different_ids()
    {
        var a = Registry().Register(NewRoot("a"));
        var b = Registry().Register(NewRoot("b"));

        Assert.NotEqual(a.ProjectId, b.ProjectId);
    }

    [Fact]
    public void Find_returns_the_registered_entry_and_null_for_others()
    {
        var root = NewRoot("repo");
        var registered = Registry().Register(root);

        var found = Registry().Find(root);

        Assert.Equal(registered.ProjectId, found?.Id);
        Assert.Null(Registry().Find(NewRoot("other")));
    }

    [Fact]
    public void Malformed_registry_file_refuses_register_instead_of_overwriting()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);
        File.WriteAllText(RegistryPath, "{ not json");

        var r = Registry().Register(NewRoot("repo"));

        Assert.False(r.Ok);
        Assert.Contains("malformed", r.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("{ not json", File.ReadAllText(RegistryPath));
    }

    [Fact]
    public void Registry_directory_is_created_through_the_injected_hook()
    {
        string? ensured = null;
        var registry = new ProjectRegistry(RegistryPath, _elevation, _volumes, Profile, p =>
        {
            ensured = p;
            Directory.CreateDirectory(p);
        });

        registry.Register(NewRoot("repo"));

        Assert.Equal(Path.GetDirectoryName(RegistryPath), ensured);
    }
}
