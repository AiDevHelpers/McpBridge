namespace AiDevHelpers.Helpers.ConfigHelper;

/// <summary>The resolved path for one config kind. Path is null only when the kind cannot be located at all.</summary>
public sealed record ConfigPathResult(ConfigFileKind Kind, string? Path, bool Exists, string? Error);
