using AiDevHelpers.Updates;
using Xunit;

namespace AiDevHelpers.Updates.Tests;

public sealed class UpdateManifestTests
{
    private const string Json = """
        {
          "Version": "1.2.3",
          "Files": [ { "Path": "bin/app.exe", "Sha256": "abc", "Size": 4 } ]
        }
        """;

    [Fact]
    public void ParsesVersionAndFiles()
    {
        var manifest = UpdateManifest.TryParse(Json);

        Assert.NotNull(manifest);
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Equal("bin/app.exe", Assert.Single(manifest.Files).Path);
    }

    [Fact]
    public void RejectsManifestWithoutFiles()
    {
        Assert.Null(UpdateManifest.TryParse("{ \"Version\": \"1.2.3\" }"));
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        var manifest = UpdateManifest.TryParse(Json)!;

        var again = UpdateManifest.TryParse(manifest.ToJson());

        Assert.NotNull(again);
        Assert.Equal(manifest.ToJson(), again.ToJson());
    }
}

