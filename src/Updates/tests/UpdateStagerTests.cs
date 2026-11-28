using System.IO.Compression;
using System.Security.Cryptography;
using AiDevHelpers.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiDevHelpers.Updates.Tests;

public sealed class UpdateStagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "updstage-" + Guid.NewGuid().ToString("N"));
    private readonly string _updates;

    public UpdateStagerTests()
    {
        _updates = Path.Combine(_root, "updates");
        Directory.CreateDirectory(_updates);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private UpdateStager NewStager(bool allowDev = true) =>
        new(_updates, new UpdatePackageVerifier(), allowDev, () => "1.0.0", NullLogger<UpdateStager>.Instance);

    private static string Hash(string content) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    // Builds a zip with one file and a matching manifest for the given version. Returns the zip path.
    private string MakeZip(string version = "2.0.0", string path = "bin/app.exe", string content = "hello", string? manifestPathOverride = null)
    {
        var manifest = new UpdateManifest(version, [new UpdateManifestFile(manifestPathOverride ?? path, Hash(content), content.Length)]);
        string zip = Path.Combine(_root, "pkg-" + Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        Write(archive, path, content);
        Write(archive, UpdateManifest.FileName, manifest.ToJson());
        return zip;
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name).Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    [Fact]
    public async Task StagesAValidZipUnderItsVersionFolder()
    {
        var result = await NewStager().StageZipAsync(MakeZip(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("2.0.0", result.Version);
        Assert.True(File.Exists(Path.Combine(_updates, "2.0.0", "bin", "app.exe")));
    }

    [Fact]
    public async Task LeavesNoStagingFolderBehind()
    {
        await NewStager().StageZipAsync(MakeZip(), CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(_updates, UpdateStager.StagingFolderName)) && Directory.EnumerateFileSystemEntries(Path.Combine(_updates, UpdateStager.StagingFolderName)).Any());
    }

    [Fact]
    public async Task ReplacesAnExistingVersionFolder()
    {
        Directory.CreateDirectory(Path.Combine(_updates, "2.0.0"));
        File.WriteAllText(Path.Combine(_updates, "2.0.0", "stale.txt"), "old");

        var result = await NewStager().StageZipAsync(MakeZip(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(File.Exists(Path.Combine(_updates, "2.0.0", "stale.txt")));
    }

    [Fact]
    public async Task RejectsAPackageThatFailsVerification()
    {
        // The release gate: an unsigned package is refused, and nothing is left under the updates root.
        var result = await NewStager(allowDev: false).StageZipAsync(MakeZip(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(Path.Combine(_updates, "2.0.0")));
    }

    [Fact]
    public async Task RejectsAZipEntryThatEscapesThePackage()
    {
        string zip = Path.Combine(_root, "slip.zip");
        var manifest = new UpdateManifest("2.0.0", [new UpdateManifestFile("ok.txt", Hash("x"), 1)]);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Write(archive, "../../escape.txt", "x");
            Write(archive, UpdateManifest.FileName, manifest.ToJson());
        }

        var result = await NewStager().StageZipAsync(zip, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task RejectsAVersionThatIsNotAFolderName()
    {
        var result = await NewStager().StageZipAsync(MakeZip(version: "2.0.0/../../x"), CancellationToken.None);

        Assert.False(result.Success);
    }
}

