using System.Security.Cryptography;
using System.Text;

namespace AiDevHelpers.Common;

/// <summary>
/// The repo's <c>.aidevtools</c> files, shared by the MCP server (reads them) and Script Helper (binds an approval to them),
/// so both compute the same hash for the same file.
/// </summary>
public static class RepoSettingsFile
{
    public const string FolderName = ".aidevtools";
    public const string SettingsFileName = "settings.json";
    public const string UserFileName = "user.json";

    private const string UserFileIgnoreLine = ".aidevtools/user.json";

    public static string SettingsPath(string repoRoot) => Path.Combine(repoRoot, FolderName, SettingsFileName);

    public static string UserFilePath(string repoRoot) => Path.Combine(repoRoot, FolderName, UserFileName);

    /// <summary>
    /// SHA-256 (hex) of a file with a leading BOM removed and line endings turned into LF, so a checkout on another platform
    /// or an editor that adds a BOM does not change the hash.
    /// </summary>
    public static string Hash(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        var text = Encoding.UTF8.GetString(bytes, start, bytes.Length - start).Replace("\r\n", "\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Adds <c>.aidevtools/user.json</c> to the repo's .gitignore when it is not already listed.</summary>
    public static void EnsureUserFileGitIgnored(string repoRoot)
    {
        var path = Path.Combine(repoRoot, ".gitignore");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        if (lines.Contains(UserFileIgnoreLine))
        {
            return;
        }

        lines.Add(UserFileIgnoreLine);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
    }
}
