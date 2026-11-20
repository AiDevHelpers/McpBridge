using AiDevHelpers.Helpers.ConfigHelper;
using Xunit;

namespace AiDevHelpers.Helpers.ConfigHelper.Tests;

public sealed class ConfigReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "config-reader-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ReadsRedactedContentInsideAllowedRoot()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(file, "{ \"ApiKey\": \"abc123\", \"Name\": \"app\" }");

        var result = ConfigReader.ReadRedacted(file, [_root]);

        Assert.True(result.Ok);
        Assert.DoesNotContain("abc123", result.Content);
        Assert.Contains("\"Name\": \"app\"", result.Content);
    }

    [Fact]
    public void RefusesPathOutsideAllowedRoots()
    {
        Directory.CreateDirectory(_root);
        var allowed = Path.Combine(_root, "repo");
        Directory.CreateDirectory(allowed);
        var outside = Path.Combine(_root, "secret.json");
        File.WriteAllText(outside, "{}");

        var result = ConfigReader.ReadRedacted(outside, [allowed]);

        Assert.False(result.Ok);
        Assert.Null(result.Content);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void RefusesTraversalOutOfAllowedRoot()
    {
        Directory.CreateDirectory(_root);
        var allowed = Path.Combine(_root, "repo");
        Directory.CreateDirectory(allowed);
        File.WriteAllText(Path.Combine(_root, "secret.json"), "{}");
        var traversal = Path.Combine(allowed, "..", "secret.json");

        var result = ConfigReader.ReadRedacted(traversal, [allowed]);

        Assert.False(result.Ok);
    }

    [Fact]
    public void MissingFileReportsError()
    {
        Directory.CreateDirectory(_root);

        var result = ConfigReader.ReadRedacted(Path.Combine(_root, "nope.json"), [_root]);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }
}
