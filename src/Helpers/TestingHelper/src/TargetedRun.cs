using System.Globalization;
using System.Xml.Linq;

namespace AiDevHelpers.Helpers.TestingHelper;

/// <summary>One test result read from a TRX file.</summary>
public sealed record TestOutcome(string Name, string Outcome, double DurationMs, string? Message);

/// <summary>Line coverage of one method: the lines in its span that the report counts, and which of them ran.</summary>
public sealed record MethodCoverage(int LinesTotal, int LinesCovered, double? Rate, IReadOnlyList<int> UncoveredLines);

/// <summary>Reads per-test results from TRX files.</summary>
public static class TrxReader
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static IReadOnlyList<TestOutcome> ReadTests(string trxXml)
    {
        var root = XDocument.Parse(trxXml).Root;
        if (root is null)
        {
            return [];
        }

        // testId -> "Namespace.Class.Method", from the test definitions; the result's own name is only a display name.
        var names = root.Descendants(Ns + "UnitTest")
            .Select(t => (Id: (string?)t.Attribute("id"), Method: t.Element(Ns + "TestMethod")))
            .Where(t => t.Id is not null && t.Method is not null)
            .ToDictionary(
                t => t.Id!,
                t => ((string?)t.Method!.Attribute("className") is { Length: > 0 } c ? c + "." : "") + (string?)t.Method.Attribute("name"),
                StringComparer.OrdinalIgnoreCase);

        var outcomes = new List<TestOutcome>();
        foreach (var result in root.Descendants(Ns + "UnitTestResult"))
        {
            var id = (string?)result.Attribute("testId");
            var name = id is not null && names.TryGetValue(id, out var full) ? full : (string?)result.Attribute("testName") ?? "";
            var duration = TimeSpan.TryParse((string?)result.Attribute("duration"), CultureInfo.InvariantCulture, out var span) ? span.TotalMilliseconds : 0;
            var message = result.Descendants(Ns + "Message").FirstOrDefault()?.Value;
            outcomes.Add(new TestOutcome(name, (string?)result.Attribute("outcome") ?? "Unknown", Math.Round(duration, 1), message));
        }

        return outcomes;
    }
}

/// <summary>Reads the coverage of a line range of one file from a Cobertura report.</summary>
public static class MethodCoverageReader
{
    /// <summary>Null when the report has no executable line of that file in the range (the method was never instrumented).</summary>
    public static MethodCoverage? ForLines(string coberturaXml, string filePath, int startLine, int endLine)
    {
        var root = XDocument.Parse(coberturaXml).Root;
        if (root is null)
        {
            return null;
        }

        var sources = root.Descendants("source").Select(s => s.Value.Trim()).Where(s => s.Length > 0).ToList();
        var target = Normalize(filePath);
        var hitsByLine = new Dictionary<int, int>();
        foreach (var cls in root.Descendants("class"))
        {
            var name = (string?)cls.Attribute("filename");
            if (name is null || !Matches(target, name, sources))
            {
                continue;
            }

            foreach (var line in cls.Elements("lines").Elements("line"))
            {
                if (int.TryParse((string?)line.Attribute("number"), out var number) && number >= startLine && number <= endLine
                    && int.TryParse((string?)line.Attribute("hits"), out var hits))
                {
                    hitsByLine[number] = Math.Max(hitsByLine.GetValueOrDefault(number), hits);
                }
            }
        }

        if (hitsByLine.Count == 0)
        {
            return null;
        }

        var uncovered = hitsByLine.Where(p => p.Value == 0).Select(p => p.Key).OrderBy(n => n).ToList();
        var covered = hitsByLine.Count - uncovered.Count;
        return new MethodCoverage(hitsByLine.Count, covered, Math.Round((double)covered / hitsByLine.Count, 4), uncovered);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    private static bool Matches(string target, string reportFile, IReadOnlyList<string> sources)
    {
        var candidate = Normalize(reportFile);
        if (candidate == target)
        {
            return true;
        }

        // A relative filename is relative to one of the report's sources.
        return !Path.IsPathRooted(reportFile)
               && (sources.Any(s => Normalize(Path.Combine(s, reportFile)) == target) || target.EndsWith("/" + candidate, StringComparison.Ordinal));
    }
}

/// <summary>Builds the <c>dotnet test</c> arguments for a run of chosen tests in one project, with or without coverage. Pure.</summary>
public static class TargetedRunArgs
{
    /// <summary>Arguments after <c>dotnet</c>. <paramref name="testNames"/> filters the run; empty runs the whole project.</summary>
    public static IReadOnlyList<string> Test(string projectPath, string runDir, IReadOnlyList<string> testNames, bool coverlet)
    {
        var args = new List<string> { "test", projectPath, "--logger", "trx", "--results-directory", runDir };
        if (testNames.Count > 0)
        {
            args.Add("--filter");
            args.Add(string.Join("|", testNames.Select(n => "FullyQualifiedName=" + n)));
        }

        if (coverlet)
        {
            args.Add("--collect");
            args.Add("XPlat Code Coverage");
        }

        return args;
    }

    /// <summary>Arguments for <c>dotnet-coverage</c> wrapping the same test run, writing a Cobertura report.</summary>
    public static IReadOnlyList<string> DotnetCoverage(IReadOnlyList<string> testArgs, string coberturaPath) =>
        ["collect", "dotnet " + string.Join(" ", testArgs.Select(Quote)), "-f", "cobertura", "-o", coberturaPath];

    private static string Quote(string arg) => arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || "-_./\\:=".Contains(c)) ? arg : "\"" + arg + "\"";
}
