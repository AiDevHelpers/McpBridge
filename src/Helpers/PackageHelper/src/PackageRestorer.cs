namespace AiDevHelpers.Helpers.PackageHelper;

/// <summary>The outcome of a restore. Output is the tail of what dotnet printed, so a failure shows its error lines.</summary>
public sealed record PackageRestoreResult(bool Ok, int ExitCode, string Output);

/// <summary>Restores the NuGet packages of a solution (<c>dotnet restore</c>). Edits no project file.</summary>
public sealed class PackageRestorer(IPackageRunner runner)
{
    /// <summary>The most output lines returned; a long restore log is cut to its last lines.</summary>
    public const int MaxLines = 60;

    public async Task<PackageRestoreResult> RestoreAsync(string workingDirectory, string solution, CancellationToken ct = default)
    {
        var (exit, output) = await runner.RunAsync(workingDirectory, ["restore", solution], ct);
        var lines = output.Replace("\r\n", "\n").TrimEnd().Split('\n');
        var tail = string.Join('\n', lines.Length > MaxLines ? lines[^MaxLines..] : lines);
        return new PackageRestoreResult(exit == 0, exit, tail);
    }
}
