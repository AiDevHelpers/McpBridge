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

    /// <summary>The nearest folder at or above <paramref name="folder"/> with a <c>.git</c> folder or file (a worktree or submodule has a file), or null.</summary>
    public static string? FindRepoRootFromFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;

        for (var dir = Path.GetFullPath(folder); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var git = Path.Combine(dir, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return dir;
        }

        return null;
    }

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

    /// <summary>
    /// Edits this user's <c>user.json</c> in place: reads it (an empty object when missing), lets <paramref name="edit"/> change
    /// the keys it owns, and writes it back, so every other key survives. A file that is not valid JSON is not overwritten.
    /// Adds the file to .gitignore.
    /// </summary>
    public static void UpdateUserFile(string repoRoot, Action<System.Text.Json.Nodes.JsonObject> edit)
    {
        var path = UserFilePath(repoRoot);
        System.Text.Json.Nodes.JsonObject root;
        if (File.Exists(path))
        {
            try
            {
                root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) as System.Text.Json.Nodes.JsonObject
                    ?? throw new IOException($"{path} is not a JSON object; fix or remove it first.");
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new IOException($"{path} is not valid JSON; fix or remove it first.", ex);
            }
        }
        else
        {
            root = new System.Text.Json.Nodes.JsonObject();
        }

        edit(root);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(temp, path, overwrite: true);
        EnsureUserFileGitIgnored(repoRoot);
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
