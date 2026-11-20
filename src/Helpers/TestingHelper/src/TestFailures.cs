using System.Xml.Linq;

namespace AiDevHelpers.Helpers.TestingHelper;

/// <summary>One failed test, read from a TRX file.</summary>
public sealed record TestFailure(string TestName, string Message, string Trx, DateTimeOffset Timestamp);

/// <summary>Reads failed tests from TRX content. Pure: the caller supplies the text.</summary>
public static class TrxFailures
{
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static IReadOnlyList<TestFailure> Parse(string trxXml, string trxPath)
    {
        var doc = XDocument.Parse(trxXml);
        var now = DateTimeOffset.UtcNow;
        return doc.Descendants(Trx + "UnitTestResult")
            .Where(r => string.Equals((string?)r.Attribute("outcome"), "Failed", StringComparison.OrdinalIgnoreCase))
            .Select(r => new TestFailure(
                TestName: (string?)r.Attribute("testName") ?? string.Empty,
                Message: (string?)r.Descendants(Trx + "Message").FirstOrDefault() ?? string.Empty,
                Trx: trxPath,
                Timestamp: now))
            .ToList();
    }
}

/// <summary>Holds failures until a caller drains them. Thread-safe.</summary>
public sealed class TestFailureQueue
{
    private readonly object _gate = new();
    private readonly List<TestFailure> _items = [];

    public void Add(IEnumerable<TestFailure> failures)
    {
        lock (_gate)
        {
            _items.AddRange(failures);
        }
    }

    /// <summary>Returns everything queued and clears the queue.</summary>
    public IReadOnlyList<TestFailure> Drain()
    {
        lock (_gate)
        {
            var copy = _items.ToList();
            _items.Clear();
            return copy;
        }
    }
}

/// <summary>
/// Watches the TRX folder for new results. Each new TRX file is parsed once; its failures go to the queue and are returned.
/// The caller decides when to poll (the MCP server polls while hot reload is active).
/// </summary>
public sealed class TestMonitor(TestFailureQueue queue)
{
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TestFailure> Poll(string trxDirectory)
    {
        if (!Directory.Exists(trxDirectory))
        {
            return [];
        }

        var fresh = Directory.EnumerateFiles(trxDirectory, "*.trx", SearchOption.AllDirectories)
            .Where(f => !_seen.Contains(f))
            .OrderBy(File.GetLastWriteTimeUtc)
            .ToList();

        var found = new List<TestFailure>();
        foreach (var file in fresh)
        {
            _seen.Add(file);
            found.AddRange(TrxFailures.Parse(File.ReadAllText(file), file));
        }

        queue.Add(found);
        return found;
    }
}
