using System.Xml.Linq;

namespace AiDevHelpers.Helpers.TestingHelper;

/// <summary>Coverage tool choice (D4).</summary>
public enum CoverageProvider
{
    Coverlet,
    VisualStudio,
}

/// <summary>Default provider: Coverlet when the repo references it, otherwise Visual Studio (D4).</summary>
public static class CoverageProviderSelector
{
    public static CoverageProvider Select(IEnumerable<string> projectFileContents)
    {
        var referencesCoverlet = projectFileContents.Any(text =>
            text.Contains("coverlet.collector", StringComparison.OrdinalIgnoreCase)
            || text.Contains("coverlet.msbuild", StringComparison.OrdinalIgnoreCase));
        return referencesCoverlet ? CoverageProvider.Coverlet : CoverageProvider.VisualStudio;
    }
}

/// <summary>Builds the argument lists for each coverage tool. Pure, so the exact commands are unit-tested.</summary>
public static class CoverageArgs
{
    public static IReadOnlyList<string> Coverlet(string solutionPath, string resultsDirectory) =>
        ["test", solutionPath, "--collect", "XPlat Code Coverage", "--results-directory", resultsDirectory];

    public static IReadOnlyList<string> DotnetCoverage(string solutionPath, string coberturaPath) =>
        ["collect", $"dotnet test \"{solutionPath}\"", "-f", "cobertura", "-o", coberturaPath];
}

/// <summary>Reads the overall line rate from a Cobertura report.</summary>
public static class CoverageSummary
{
    public static double? LineRate(string coberturaXml)
    {
        var rate = (string?)XDocument.Parse(coberturaXml).Root?.Attribute("line-rate");
        return double.TryParse(rate, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
