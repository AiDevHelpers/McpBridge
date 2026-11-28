using System.Security.Cryptography;

namespace AiDevHelpers.Updates;

/// <summary>
/// Builds the manifest for a package folder at release time: one entry per file, with its size and SHA-256. The manifest and its signature
/// are not listed (they describe the package, they are not part of it). Used by the release workflow before signing.
/// </summary>
public static class UpdateManifestWriter
{
    private const string SignatureFileName = UpdateManifest.FileName + ".sig";

    /// <summary>Builds the manifest for every file under <paramref name="packageDir"/>, sorted by path.</summary>
    public static UpdateManifest Build(string packageDir, string version, DateTimeOffset? notAfter)
    {
        var files = new List<UpdateManifestFile>();
        foreach (string full in Directory.EnumerateFiles(packageDir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(packageDir, full).Replace(Path.DirectorySeparatorChar, '/');
            if (relative is UpdateManifest.FileName or SignatureFileName)
            {
                continue;
            }

            byte[] bytes = File.ReadAllBytes(full);
            files.Add(new UpdateManifestFile(relative, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength));
        }

        return new UpdateManifest(version, files, notAfter);
    }

    /// <summary>Writes <c>manifest.json</c> into the package folder. Run it before signing.</summary>
    public static void WriteManifest(string packageDir, string version, DateTimeOffset? notAfter) =>
        File.WriteAllText(Path.Combine(packageDir, UpdateManifest.FileName), Build(packageDir, version, notAfter).ToJson());
}
