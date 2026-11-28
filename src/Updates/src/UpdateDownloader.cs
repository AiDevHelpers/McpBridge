namespace AiDevHelpers.Updates;

/// <summary>Result of <see cref="UpdateDownloader.DownloadAndStageAsync"/>. <paramref name="Stage"/> is set when the package reached the stager.</summary>
public sealed record UpdateDownloadResult(bool Success, string Message, UpdateStageResult? Stage);

/// <summary>
/// Downloads a release's package zip, checks it, and hands it to the stager (unit 10b). The download goes to its own folder with a fixed
/// file name, is capped in size, must come from an allowed https host (also after redirects), must match the advertised size, and must
/// stage as the version the tag names. Whatever happens, the download is deleted.
/// </summary>
public sealed class UpdateDownloader(HttpClient http, IUpdateStager stager, string updatesRoot, long maxBytes = UpdateDownloader.DefaultMaxBytes) : IUpdateDownloader
{
    /// <summary>Largest package accepted: 1 GB.</summary>
    public const long DefaultMaxBytes = 1024L * 1024 * 1024;

    /// <summary>Folder under the updates root that holds downloads while they are checked.</summary>
    public const string DownloadFolderName = ".download";

    private const string PackageFileName = "package.zip";
    private static readonly string[] AllowedHosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    /// <summary>Downloads, checks and stages the package for <paramref name="expectedVersion"/>.</summary>
    public async Task<UpdateDownloadResult> DownloadAndStageAsync(GitHubReleaseAsset asset, string expectedVersion, CancellationToken ct)
    {
        if (!IsAllowed(asset.DownloadUrl))
        {
            return Fail("the download URL is not https on an allowed host");
        }

        string folder = Path.Combine(updatesRoot, DownloadFolderName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string zip = Path.Combine(folder, PackageFileName);
            long written;
            using (var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl))
            using (HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                Uri finalUri = response.RequestMessage?.RequestUri ?? asset.DownloadUrl;
                if (!IsAllowed(finalUri))
                {
                    return Fail("the download was redirected to a URL that is not allowed");
                }

                if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
                {
                    return Fail($"the download is {declared} bytes, over the {maxBytes} byte limit");
                }

                await using Stream input = await response.Content.ReadAsStreamAsync(ct);
                await using FileStream output = File.Create(zip);
                written = await CopyCappedAsync(input, output, maxBytes, ct);
            }

            if (written < 0)
            {
                return Fail($"the download is over the {maxBytes} byte limit");
            }

            if (written != asset.Size)
            {
                return Fail($"downloaded {written} bytes, but the release says {asset.Size}");
            }

            UpdateStageResult stage = await stager.StageZipAsync(zip, ct);
            if (!stage.Success)
            {
                return new UpdateDownloadResult(false, stage.Message, stage);
            }

            if (!string.Equals(stage.Version, expectedVersion, StringComparison.Ordinal))
            {
                if (stage.StagedDir is not null && Directory.Exists(stage.StagedDir))
                {
                    Directory.Delete(stage.StagedDir, recursive: true);
                }

                return new UpdateDownloadResult(false, $"the package is version {stage.Version}, the release is {expectedVersion}", stage);
            }

            return new UpdateDownloadResult(true, "package staged", stage);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private static bool IsAllowed(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && AllowedHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase);

    // Copies at most maxBytes. Returns the byte count, or -1 when the source is larger.
    private static async Task<long> CopyCappedAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > limit)
            {
                return -1;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return total;
    }

    private static UpdateDownloadResult Fail(string message) => new(false, message, null);
}
