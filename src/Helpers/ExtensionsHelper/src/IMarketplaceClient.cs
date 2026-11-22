using System.Net.Http.Json;
using System.Text.Json;

namespace AiDevHelpers.Helpers.ExtensionsHelper;

/// <summary>Visual Studio Marketplace access: find an extension by id and download its .vsix.</summary>
public interface IMarketplaceClient
{
    Task<MarketplaceExtension?> FindAsync(string id, CancellationToken ct = default);

    Task<string> DownloadAsync(string vsixUrl, string destinationPath, CancellationToken ct = default);
}

/// <summary>Real client. The gallery query shape is unverified against the live endpoint.</summary>
public sealed class HttpMarketplaceClient(HttpClient http) : IMarketplaceClient
{
    private const string QueryUrl = "https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery";

    public async Task<MarketplaceExtension?> FindAsync(string id, CancellationToken ct = default)
    {
        var body = new
        {
            filters = new[]
            {
                new
                {
                    criteria = new[] { new { filterType = 7, value = id } },
                    pageNumber = 1,
                    pageSize = 1,
                    sortBy = 0,
                    sortOrder = 0,
                },
            },
            assetTypes = Array.Empty<string>(),
            flags = 914,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, QueryUrl)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Accept", "application/json;api-version=3.0-preview.1");

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return MarketplaceResponse.ParseExtension(await response.Content.ReadAsStringAsync(ct), id);
    }

    public async Task<string> DownloadAsync(string vsixUrl, string destinationPath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using var response = await http.GetAsync(vsixUrl, ct);
        response.EnsureSuccessStatusCode();
        await using var file = File.Create(destinationPath);
        await response.Content.CopyToAsync(file, ct);
        return destinationPath;
    }
}

/// <summary>Reads the gallery query response. Pure, so it is tested with a recorded fixture.</summary>
public static class MarketplaceResponse
{
    private const string VsixAssetType = "Microsoft.VisualStudio.Services.VSIXPackage";

    // Visual Studio items name the package "<Name>.vsix" (for example BookmarkStudio.vsix), and some use the generic type.
    private static bool IsVsixAsset(string? assetType) =>
        assetType == VsixAssetType || (assetType is not null && assetType.EndsWith(".vsix", StringComparison.OrdinalIgnoreCase));

    public static MarketplaceExtension? ParseExtension(string json, string id)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results))
        {
            return null;
        }

        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("extensions", out var extensions))
            {
                continue;
            }

            foreach (var ext in extensions.EnumerateArray())
            {
                var publisher = ext.GetProperty("publisher").GetProperty("publisherName").GetString() ?? string.Empty;
                var fullId = publisher + "." + ext.GetProperty("extensionName").GetString();
                if (!string.Equals(fullId, id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var version = ext.GetProperty("versions")[0];
                var vsixUrl = version.GetProperty("files").EnumerateArray()
                    .First(f => IsVsixAsset(f.GetProperty("assetType").GetString()))
                    .GetProperty("source").GetString() ?? string.Empty;

                return new MarketplaceExtension(
                    Id: fullId,
                    Version: version.GetProperty("version").GetString() ?? string.Empty,
                    DisplayName: ext.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? fullId : fullId,
                    Publisher: publisher,
                    Description: ext.TryGetProperty("shortDescription", out var sd) ? sd.GetString() ?? string.Empty : string.Empty,
                    VsixUrl: vsixUrl);
            }
        }

        return null;
    }
}
