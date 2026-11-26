namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

/// <summary>Copies the Fixtures folder to a new temp directory, so the tests never write into the repo.</summary>
public sealed class FixtureCopy : IDisposable
{
    private FixtureCopy(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public string SolutionPath => Path.Combine(Root, "Fixtures.slnx");

    public static FixtureCopy Create()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var root = Path.Combine(Path.GetTempPath(), "roslyn-fixtures", Guid.NewGuid().ToString("N"));
        CopyDirectory(source, root);
        return new FixtureCopy(root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }
}
