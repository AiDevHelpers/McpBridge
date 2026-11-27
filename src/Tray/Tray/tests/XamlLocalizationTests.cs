using System.Text.RegularExpressions;
using Xunit;

namespace AiDevHelpers.Tray.Tests;

/// <summary>Every user-facing text in the tray's XAML comes from Strings.resx, not a literal in the markup.</summary>
public sealed class XamlLocalizationTests
{
    private static readonly Regex LiteralText = new(
        "\\s(Text|Content|Title|Header)=\"(?![{])([^\"]+)\"",
        RegexOptions.CultureInvariant);

    private static string SourceFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Tray", "Tray", "src", "StatusWindow.xaml")))
        {
            dir = dir.Parent;
        }

        return dir is null ? throw new InvalidOperationException("Could not find the tray source folder.") : Path.Combine(dir.FullName, "src", "Tray", "Tray", "src");
    }

    [Fact]
    public void NoXamlWindowHasALiteralUserText()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(SourceFolder(), "*.xaml"))
        {
            foreach (Match match in LiteralText.Matches(File.ReadAllText(file)))
            {
                offenders.Add($"{Path.GetFileName(file)}: {match.Groups[1].Value}=\"{match.Groups[2].Value}\"");
            }
        }

        Assert.Empty(offenders);
    }
}
