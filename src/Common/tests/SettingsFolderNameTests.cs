using Xunit;

namespace AiDevHelpers.Common.Tests;

/// <summary>The repo settings folder is <c>.aidevhelpers</c>. The old name must not come back as a literal.</summary>
public sealed class SettingsFolderNameTests
{
    // Built from parts so this file does not match itself.
    private static readonly string LegacyName = "." + "aidevtools";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AiDevHelpers.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("AiDevHelpers.slnx not found above the test output folder.");
    }

    [Fact]
    public void FolderNameIsAidevhelpers() => Assert.Equal(".aidevhelpers", RepoSettingsFile.FolderName);

    [Fact]
    public void NoSourceOrDocFileUsesTheLegacyFolderName()
    {
        var root = RepoRoot();
        var files = new[] { "*.cs", "*.resx", "*.md", "*.ps1" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, "src"), p, SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "scripts"), "*.ps1", SearchOption.TopDirectoryOnly))
            .Append(Path.Combine(root, ".gitignore"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}"));

        var hits = files.Where(f => File.ReadAllText(f).Contains(LegacyName, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.True(hits.Count == 0, "Legacy settings folder name found in: " + string.Join(", ", hits));
    }

    [Fact]
    public void RepoHasNoLegacyFolder() => Assert.False(Directory.Exists(Path.Combine(RepoRoot(), LegacyName)));
}
