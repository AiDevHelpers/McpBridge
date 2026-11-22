using System.Security.Cryptography;
using System.Text;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>One input to a fingerprint: a script, a dot-sourced file, or the interpreter id.</summary>
public sealed record FingerprintPart(string Name, byte[] Content);

public static class Fingerprint
{
    public static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data));

    /// <summary>
    /// Combines parts into one digest. Parts are sorted by name so order does not matter.
    /// Every field is length-prefixed, so no name can forge another layout. Duplicate names
    /// are rejected because they would make the digest ambiguous.
    /// </summary>
    public static string Compute(IEnumerable<FingerprintPart> parts)
    {
        var ordered = parts.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();

        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Name == ordered[i - 1].Name)
                throw new ArgumentException($"duplicate fingerprint part name: {ordered[i].Name}", nameof(parts));
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        w.Write(ordered.Count);
        foreach (var part in ordered)
        {
            WriteField(w, Encoding.UTF8.GetBytes(part.Name));
            WriteField(w, SHA256.HashData(part.Content));
        }

        w.Flush();
        return Sha256Hex(ms.ToArray());
    }

    /// <summary>Writes a length-prefixed field. Shared by the approvals payload.</summary>
    internal static void WriteField(BinaryWriter w, byte[] bytes)
    {
        w.Write(bytes.Length);
        w.Write(bytes);
    }
}
