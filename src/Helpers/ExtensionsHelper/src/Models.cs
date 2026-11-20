namespace AiDevHelpers.Helpers.ExtensionsHelper;

/// <summary>The IDE an extension belongs to.</summary>
public enum ExtensionIde
{
    VsCode,
    VisualStudio,
}

/// <summary>An installed or Marketplace-listed extension. Fields are null when the source does not provide them.</summary>
public sealed record ExtensionInfo(
    ExtensionIde Ide,
    string Id,
    string Version,
    string? DisplayName,
    string? Publisher,
    string? Description);

/// <summary>An extension found on the Visual Studio Marketplace.</summary>
public sealed record MarketplaceExtension(
    string Id,
    string Version,
    string DisplayName,
    string Publisher,
    string Description,
    string VsixUrl);

/// <summary>Captured output of a command.</summary>
public sealed record CommandResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// A planned add waiting for the user's approval. Nothing runs until ConfirmAddAsync is called with the token.
/// Extension is set only for Visual Studio, where the install needs a Marketplace download.
/// </summary>
public sealed record PendingAdd(string Token, ExtensionIde Ide, string Id, string Command, MarketplaceExtension? Extension);

/// <summary>Result of a confirmed add.</summary>
public sealed record AddResult(bool Ok, int ExitCode, string Output);
