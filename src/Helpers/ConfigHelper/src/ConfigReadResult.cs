namespace AiDevHelpers.Helpers.ConfigHelper;

/// <summary>Result of reading a config file. Content is redacted; Error is set when Ok is false.</summary>
public sealed record ConfigReadResult(bool Ok, string? Content, string? Error);
