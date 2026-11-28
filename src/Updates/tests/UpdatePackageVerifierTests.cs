using System.Security.Cryptography;
using AiDevHelpers.Updates;
using Xunit;

namespace AiDevHelpers.Updates.Tests;

public sealed class UpdatePackageVerifierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "updverify-" + Guid.NewGuid().ToString("N"));

    public UpdatePackageVerifierTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // Writes a package with one file, a matching manifest for version 2.0.0, and returns the folder.
    private string WritePackage(string version = "2.0.0", string content = "hello", string path = "bin/app.exe", bool writeManifest = true)
    {
        string full = Path.Combine(_dir, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (writeManifest)
        {
            var manifest = new UpdateManifest(version, [new UpdateManifestFile(path, Hash(content), content.Length)]);
            File.WriteAllText(Path.Combine(_dir, UpdateManifest.FileName), manifest.ToJson());
        }

        return _dir;
    }

    private static string Hash(string content) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    private static Task<UpdatePackageVerification> Verify(string dir, string current = "1.0.0", bool allowDev = true) =>
        new UpdatePackageVerifier().VerifyAsync(dir, current, allowDev, CancellationToken.None);

    [Fact]
    public async Task DevPackageAcceptedWhenAllowed()
    {
        var result = await Verify(WritePackage());

        Assert.Equal(UpdatePackageStatus.Valid, result.Status);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task UnsignedReleasePackageRejected()
    {
        var result = await Verify(WritePackage(), allowDev: false);

        Assert.Equal(UpdatePackageStatus.SignatureInvalid, result.Status);
    }

    [Fact]
    public async Task TamperedFileRejected()
    {
        string dir = WritePackage(content: "hello");
        File.WriteAllText(Path.Combine(dir, "bin", "app.exe"), "HELLO");

        var result = await Verify(dir);

        Assert.Equal(UpdatePackageStatus.HashMismatch, result.Status);
    }

    [Fact]
    public async Task SizeMismatchRejected()
    {
        string dir = WritePackage(content: "hello");
        File.WriteAllText(Path.Combine(dir, "bin", "app.exe"), "hello-and-more");

        var result = await Verify(dir);

        Assert.Equal(UpdatePackageStatus.SizeMismatch, result.Status);
    }

    [Fact]
    public async Task MissingFileRejected()
    {
        string dir = WritePackage();
        File.Delete(Path.Combine(dir, "bin", "app.exe"));

        var result = await Verify(dir);

        Assert.Equal(UpdatePackageStatus.FileMissing, result.Status);
    }

    [Fact]
    public async Task UnsafePathRejected()
    {
        string dir = WritePackage(path: "bin/app.exe");
        var manifest = new UpdateManifest("2.0.0", [new UpdateManifestFile("../escape.exe", Hash("x"), 1)]);
        File.WriteAllText(Path.Combine(dir, UpdateManifest.FileName), manifest.ToJson());

        var result = await Verify(dir);

        Assert.Equal(UpdatePackageStatus.UnsafePath, result.Status);
    }

    [Fact]
    public async Task VersionNotNewerRejected()
    {
        var result = await Verify(WritePackage(version: "1.0.0"), current: "1.0.0", allowDev: false);

        Assert.Equal(UpdatePackageStatus.VersionNotNewer, result.Status);
    }

    [Fact]
    public async Task OlderVersionRejectedEvenForDevPackages()
    {
        var result = await Verify(WritePackage(version: "0.9.0"), current: "1.0.0", allowDev: true);

        Assert.Equal(UpdatePackageStatus.VersionNotNewer, result.Status);
    }

    [Fact]
    public async Task MissingManifestRejected()
    {
        var result = await Verify(WritePackage(writeManifest: false));

        Assert.Equal(UpdatePackageStatus.ManifestMissing, result.Status);
    }
}

