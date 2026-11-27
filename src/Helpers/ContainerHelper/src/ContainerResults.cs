namespace AiDevHelpers.Helpers.ContainerHelper;

/// <summary>Which runtime a request resolved to, or why none did.</summary>
public sealed record RuntimeDetection(string? Runtime, string? Error)
{
    public bool Ok => Runtime is not null;
}

public sealed record ContainerStatusResult(
    bool Ok,
    string? Error,
    string? Runtime,
    string? Container,
    string? State,
    string? Health,
    int? ExitCode,
    string? FinishedAt);

public sealed record ContainerLogsResult(
    bool Ok,
    string? Error,
    string? Runtime,
    string? Container,
    int Lines,
    string Logs,
    bool Capped);

public sealed record ContainerActionResult(
    bool Ok,
    string? Error,
    string? Runtime,
    string? Container,
    string? Action,
    ContainerStatusResult? Status);

public sealed record ContainerExecResult(
    bool Ok,
    string? Error,
    string? Runtime,
    string? Container,
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    bool Capped);
