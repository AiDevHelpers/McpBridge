using AiDevHelpers.Helpers.ExtensionsHelper;

namespace AiDevHelpers.Helpers.ExtensionsHelper.Tests;

/// <summary>Records every command instead of running it.</summary>
internal sealed class FakeRunner : ICommandRunner
{
    public List<string> Calls { get; } = [];

    public Func<string, IReadOnlyList<string>, CommandResult> Handler { get; set; } =
        (_, _) => new CommandResult(0, string.Empty, string.Empty);

    public Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        Calls.Add(file + " " + string.Join(" ", args));
        return Task.FromResult(Handler(file, args));
    }
}

/// <summary>Returns a fixed extension and a fixed download path; never touches the network.</summary>
internal sealed class FakeMarketplace(MarketplaceExtension? extension) : IMarketplaceClient
{
    public List<string> Downloads { get; } = [];

    public Task<MarketplaceExtension?> FindAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(extension is not null && string.Equals(extension.Id, id, StringComparison.OrdinalIgnoreCase)
            ? extension
            : null);

    public Task<string> DownloadAsync(string vsixUrl, string destinationPath, CancellationToken ct = default)
    {
        Downloads.Add(vsixUrl);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.WriteAllText(destinationPath, "vsix");
        return Task.FromResult(destinationPath);
    }
}
