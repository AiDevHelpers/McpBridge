using System.Net;
using AiDevHelpers.Updates;
using Xunit;

namespace AiDevHelpers.Updates.Tests;

public sealed class GitHubReleaseClientTests
{
    // Answers every request with the given JSON and records the last request.
    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string Release(string tag, bool prerelease, params string[] assetNames)
    {
        string assets = string.Join(",", assetNames.Select(n =>
            $"{{\"name\":\"{n}\",\"size\":1234,\"browser_download_url\":\"https://github.com/o/r/releases/download/{tag}/{n}\"}}"));
        return $"{{\"tag_name\":\"{tag}\",\"draft\":false,\"prerelease\":{(prerelease ? "true" : "false")},\"assets\":[{assets}]}}";
    }

    private static (GitHubReleaseClient Client, StubHandler Handler) ClientFor(string json)
    {
        var handler = new StubHandler(json);
        return (new GitHubReleaseClient(new HttpClient(handler), "AiDevHelpers", "McpBridge"), handler);
    }

    [Fact]
    public async Task FindsPackageZipAsset()
    {
        var (client, _) = ClientFor(Release("v0.2.0", false, "AiDevHelpers-package-0.2.0.zip", "setup.exe"));

        GitHubRelease? release = await client.GetLatestAsync(CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal("v0.2.0", release.Tag);
        Assert.Equal("0.2.0", release.Version);
        Assert.Equal("AiDevHelpers-package-0.2.0.zip", release.Package.Name);
        Assert.Equal(1234, release.Package.Size);
    }

    [Fact]
    public async Task NoZipAssetReturnsNull()
    {
        var (client, _) = ClientFor(Release("v0.2.0", false, "setup.exe"));

        Assert.Null(await client.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RejectsTagWithoutVersion()
    {
        var (client, _) = ClientFor(Release("latest", false, "AiDevHelpers-package-0.2.0.zip"));

        Assert.Null(await client.GetLatestAsync(CancellationToken.None));
    }

    // The package named for another version is not this release's package, even when the tag is valid.
    [Fact]
    public async Task AssetMustMatchTagVersion()
    {
        var (client, _) = ClientFor(Release("v0.2.0", false, "AiDevHelpers-package-0.1.0.zip"));

        Assert.Null(await client.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SkipsPrerelease()
    {
        var (client, _) = ClientFor(Release("v0.2.0", true, "AiDevHelpers-package-0.2.0.zip"));

        Assert.Null(await client.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SendsUserAgent()
    {
        var (client, handler) = ClientFor(Release("v0.2.0", false, "AiDevHelpers-package-0.2.0.zip"));

        await client.GetLatestAsync(CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.NotEmpty(handler.LastRequest.Headers.UserAgent);
        Assert.Equal("https://api.github.com/repos/AiDevHelpers/McpBridge/releases/latest", handler.LastRequest.RequestUri!.ToString());
    }
}
