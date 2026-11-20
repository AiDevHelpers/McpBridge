using AiDevHelpers.Helpers.ErrorHelper;
using Xunit;

namespace AiDevHelpers.Helpers.ErrorHelper.Tests;

public sealed class SuppressionSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "error-suppress-tests", Guid.NewGuid().ToString("N"));

    public SuppressionSettingsTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void LoadReadsCodesAndSourcesFromVsconfigSection()
    {
        var sln = Path.Combine(_dir, "App.sln");
        File.WriteAllText(Path.Combine(_dir, ".vsconfig"),
            "{ \"version\": \"1.0\", \"components\": [], \"aidevhelpersErrors\": { \"suppressCodes\": [\"CA1822\"], \"suppressSources\": [\"NuGet\"] } }");

        var settings = SuppressionSettings.Load(sln);

        Assert.Contains("CA1822", settings.Codes);
        Assert.Contains(DiagnosticSource.NuGet, settings.Sources);
    }

    [Fact]
    public void LoadWithoutVsconfigReturnsEmpty()
    {
        var settings = SuppressionSettings.Load(Path.Combine(_dir, "App.sln"));

        Assert.Empty(settings.Codes);
        Assert.Empty(settings.Sources);
    }

    [Fact]
    public void LoadWithoutSectionReturnsEmpty()
    {
        File.WriteAllText(Path.Combine(_dir, ".vsconfig"), "{ \"version\": \"1.0\", \"components\": [] }");

        var settings = SuppressionSettings.Load(Path.Combine(_dir, "App.sln"));

        Assert.Empty(settings.Codes);
    }
}
