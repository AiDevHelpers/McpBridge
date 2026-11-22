namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// One allowlist line from a project's .elevated.json.
/// Path is relative to the repo root; '*' is the only wildcard.
/// Args is null when no arguments are permitted.
/// </summary>
public sealed record AllowlistEntry(string Path, IReadOnlyList<string>? Args);
