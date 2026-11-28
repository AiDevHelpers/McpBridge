using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AiDevHelpers.Updates;

/// <summary>One downloadable asset of a GitHub release.</summary>
public sealed record GitHubReleaseAsset(string Name, long Size, Uri DownloadUrl);

/// <summary>The release the updater may install: its tag, the version the tag names, and the package zip for that version.</summary>
public sealed record GitHubRelease(string Tag, string Version, GitHubReleaseAsset Package);

/// <summary>
/// Reads the latest GitHub release of the product and picks its package zip. A release is usable only when its tag is
/// vMAJOR.MINOR.PATCH, it is not a draft or prerelease, and it has <c>AiDevHelpers-package-&lt;version&gt;.zip</c>.
/// Anything else returns null, so a malformed or spoofed release is never installed.
/// </summary>
public sealed class GitHubReleaseClient(HttpClient http, string owner, string repo) : IGitHubReleaseSource
{
    /// <summary>Name of the package zip for a version: <c>AiDevHelpers-package-&lt;version&gt;.zip</c>.</summary>
    public static string PackageAssetName(string version) => $"AiDevHelpers-package-{version}.zip";

    private static readonly Regex TagPattern = new(@"^v(\d+\.\d+\.\d+)$", RegexOptions.CultureInvariant);

    /// <summary>Fetches the latest release, or null when there is no usable release.</summary>
    public async Task<GitHubRelease?> GetLatestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AiDevHelpers-Updater", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using HttpResponseMessage response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        ReleaseDto? dto = await JsonSerializer.DeserializeAsync<ReleaseDto>(stream, cancellationToken: ct);
        if (dto is null || dto.Draft || dto.Prerelease || dto.TagName is null)
        {
            return null;
        }

        Match match = TagPattern.Match(dto.TagName);
        if (!match.Success)
        {
            return null;
        }

        string version = match.Groups[1].Value;
        string assetName = PackageAssetName(version);
        AssetDto? asset = dto.Assets?.FirstOrDefault(a => a.Name == assetName);
        if (asset is null || asset.BrowserDownloadUrl is null || !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out Uri? url))
        {
            return null;
        }

        return new GitHubRelease(dto.TagName, version, new GitHubReleaseAsset(assetName, asset.Size, url));
    }

    private sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; init; }
        [JsonPropertyName("draft")] public bool Draft { get; init; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }
        [JsonPropertyName("assets")] public List<AssetDto>? Assets { get; init; }
    }

    private sealed class AssetDto
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("size")] public long Size { get; init; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; init; }
    }
}
