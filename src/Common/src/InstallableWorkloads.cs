using System.Security.Cryptography;
using System.Text;

namespace AiDevHelpers.Common;

/// <summary>A .NET workload the elevated installer may add. <see cref="Name"/> is what callers pass; the id is never taken from a caller.</summary>
public sealed record InstallableWorkload(string Name, string Display);

/// <summary>
/// The only .NET workloads the elevated install path may add. The installed script carries this table, and its hash is pinned in
/// <c>scripts/workload-install.sha256</c>: changing the table without updating the pin makes the pin test fail.
/// </summary>
public static class InstallableWorkloads
{
    public static readonly IReadOnlyList<InstallableWorkload> All =
    [
        new("maui", ".NET MAUI"),
        new("android", ".NET Android"),
        new("ios", ".NET iOS"),
        new("wasm-tools", "WebAssembly tools"),
    ];

    public static InstallableWorkload? TryGet(string? name) =>
        All.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The table as the script embeds it: one <c>name</c> per line, in order, LF line endings.</summary>
    public static string TableText() => string.Join("\n", All.Select(w => w.Name)) + "\n";

    /// <summary>SHA-256 of <see cref="TableText"/>, upper-case hex. Compared with the pin file.</summary>
    public static string TableHash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TableText())));
}
