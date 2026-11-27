namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// One allowlist line from a project's .elevated.json.
/// Path is relative to the repo root; '*' is the only wildcard.
/// Args is null when no arguments are permitted.
/// </summary>
/// <summary>
/// One allowed script. Pins are repo files the script reads from its own folder (for example a hash file beside it); they are part
/// of the approved fingerprint and are staged beside the script, so the script finds them where it expects them.
/// </summary>
public sealed record AllowlistEntry(string Path, IReadOnlyList<string>? Args, IReadOnlyList<string>? Pins = null);
