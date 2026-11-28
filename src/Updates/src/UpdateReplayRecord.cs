using System.Security.Cryptography;
using System.Text.Json;

namespace AiDevHelpers.Updates;

/// <summary>
/// Remembers the manifest hash of the last applied package. A package offered again with the same manifest is a replay (for example after a
/// rollback) and is refused. The record lives in the updates root, which only SYSTEM and Administrators can write (UpdatesFolderAcl).
/// </summary>
public sealed class UpdateReplayRecord
{
    /// <summary>File name of the record inside the updates root.</summary>
    public const string FileName = "last-applied.json";

    private readonly string _path;

    public UpdateReplayRecord(string updatesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(updatesRoot);
        _path = Path.Combine(updatesRoot, FileName);
    }

    /// <summary>True when the package's manifest is the one recorded as last applied.</summary>
    public async Task<bool> IsReplayAsync(string packageDir, CancellationToken ct)
    {
        string? last = await ReadLastHashAsync(ct);
        return last is not null && string.Equals(last, await ManifestHashAsync(packageDir, ct), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Records the package as the last one applied. The file is replaced in one move, so a crash never leaves it half-written.</summary>
    public async Task RecordAsync(string packageDir, CancellationToken ct)
    {
        string hash = await ManifestHashAsync(packageDir, ct);
        string temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new Entry(hash)), ct);
        File.Move(temp, _path, overwrite: true);
    }

    private static async Task<string> ManifestHashAsync(string packageDir, CancellationToken ct)
    {
        byte[] manifest = await File.ReadAllBytesAsync(Path.Combine(packageDir, UpdateManifest.FileName), ct);
        return Convert.ToHexString(SHA256.HashData(manifest));
    }

    private async Task<string?> ReadLastHashAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        await using FileStream stream = File.OpenRead(_path);
        Entry? entry = await JsonSerializer.DeserializeAsync<Entry>(stream, cancellationToken: ct);
        return entry?.ManifestSha256;
    }

    /// <summary>The stored record.</summary>
    public sealed record Entry(string ManifestSha256);
}
