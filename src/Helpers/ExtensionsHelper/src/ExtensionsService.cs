using System.Collections.Concurrent;
using System.Text.Json;

namespace AiDevHelpers.Helpers.ExtensionsHelper;

/// <summary>
/// Lists, describes and adds extensions. Adds are two-step: PlanAddAsync returns the exact command and a token,
/// and nothing runs until ConfirmAddAsync is called with that token (D6, D9, D13).
/// </summary>
public sealed class ExtensionsService
{
    private const string VsixDirName = "aidevhelpers-ext";

    private readonly ICommandRunner _runner;
    private readonly IMarketplaceClient _market;
    private readonly string _vsCodeExtensionsRoot;
    private readonly Func<string?> _locateVsixInstaller;
    private readonly ConcurrentDictionary<string, PendingAdd> _pending = new();

    public ExtensionsService(
        ICommandRunner runner,
        IMarketplaceClient market,
        string vsCodeExtensionsRoot,
        Func<string?> locateVsixInstaller)
    {
        _runner = runner;
        _market = market;
        _vsCodeExtensionsRoot = vsCodeExtensionsRoot;
        _locateVsixInstaller = locateVsixInstaller;
    }

    /// <summary>Lists VS Code extensions from the code CLI (id@version lines).</summary>
    public async Task<IReadOnlyList<ExtensionInfo>> ListVsCodeAsync(CancellationToken ct = default)
    {
        var result = await _runner.RunAsync("code", ["--list-extensions", "--show-versions"], ct);
        var list = new List<ExtensionInfo>();
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = line.LastIndexOf('@');
            if (at <= 0)
            {
                continue;
            }

            list.Add(new ExtensionInfo(ExtensionIde.VsCode, line[..at], line[(at + 1)..], null, null, null));
        }

        return list;
    }

    /// <summary>Reads package.json of the newest installed copy of a VS Code extension. Null when not installed.</summary>
    public ExtensionInfo? GetVsCodeInfo(string id)
    {
        if (!Directory.Exists(_vsCodeExtensionsRoot))
        {
            return null;
        }

        var folder = Directory.EnumerateDirectories(_vsCodeExtensionsRoot, id + "-*")
            .Where(d => Path.GetFileName(d).StartsWith(id + "-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (folder is null)
        {
            return null;
        }

        var manifest = Path.Combine(folder, "package.json");
        if (!File.Exists(manifest))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        var root = doc.RootElement;
        return new ExtensionInfo(
            ExtensionIde.VsCode,
            id,
            Str(root, "version") ?? string.Empty,
            Str(root, "displayName"),
            Str(root, "publisher"),
            Str(root, "description"));
    }

    /// <summary>Describes a Visual Studio extension from the Marketplace. Null when not found.</summary>
    public Task<ExtensionInfo?> GetVisualStudioInfoAsync(string id, CancellationToken ct = default) =>
        Map(_market.FindAsync(id, ct));

    /// <summary>Plans an add. Runs nothing for VS Code; for Visual Studio it only checks the Marketplace.</summary>
    public async Task<PendingAdd> PlanAddAsync(ExtensionIde ide, string id, CancellationToken ct = default)
    {
        if (ide == ExtensionIde.VsCode)
        {
            return Store(new PendingAdd(string.Empty, ide, id, $"code --install-extension {id}", null));
        }

        var ext = await _market.FindAsync(id, ct)
            ?? throw new ArgumentException(Strings.Format("VisualStudioExtensionNotFound", id), nameof(id));
        return Store(new PendingAdd(
            string.Empty,
            ide,
            id,
            Strings.Format("VsixManualInstallStep", ext.VsixUrl),
            ext));
    }

    /// <summary>Runs a planned add once. An unknown or reused token is refused.</summary>
    public async Task<AddResult> ConfirmAddAsync(string token, CancellationToken ct = default)
    {
        if (!_pending.TryRemove(token, out var pending))
        {
            return new AddResult(false, -1, Strings.Get("UnknownOrUsedToken"));
        }

        if (pending.Ide == ExtensionIde.VsCode)
        {
            var run = await _runner.RunAsync("code", ["--install-extension", pending.Id], ct);
            return new AddResult(run.ExitCode == 0, run.ExitCode, run.Stdout + run.Stderr);
        }

        var installer = _locateVsixInstaller();
        if (installer is null)
        {
            return new AddResult(false, -1, Strings.Get("VsixInstallerNotFound"));
        }

        var destination = Path.Combine(Path.GetTempPath(), VsixDirName, pending.Id + ".vsix");
        await _market.DownloadAsync(pending.Extension!.VsixUrl, destination, ct);
        var install = await _runner.RunAsync(installer, [destination, "/q"], ct);
        return new AddResult(install.ExitCode == 0, install.ExitCode, install.Stdout + install.Stderr);
    }

    private PendingAdd Store(PendingAdd plan)
    {
        var token = Guid.NewGuid().ToString("N");
        var stored = plan with { Token = token };
        _pending[token] = stored;
        return stored;
    }

    private static async Task<ExtensionInfo?> Map(Task<MarketplaceExtension?> task)
    {
        var ext = await task;
        return ext is null
            ? null
            : new ExtensionInfo(ExtensionIde.VisualStudio, ext.Id, ext.Version, ext.DisplayName, ext.Publisher, ext.Description);
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
