using System.Text.Json.Nodes;

namespace AiDevHelpers.Helpers.WslHelper;

/// <summary>One row of <c>wsl.exe -l -v</c>.</summary>
public sealed record WslDistro(string Name, bool IsDefault, string State, int Version);

/// <summary>Result of <c>status</c>. A missing <c>wsl.exe</c> is <c>Ok = true, Available = false</c>, not an error.</summary>
public sealed record WslStatusResult(
    bool Ok,
    string? Error,
    bool Available,
    int DistroCount,
    IReadOnlyList<WslDistro> Distros,
    string? DefaultDistro);

/// <summary>Result of <c>health</c>. A failed probe is <c>Ok = true, Healthy = false</c> with <see cref="Detail" />.</summary>
public sealed record WslHealthResult(
    bool Ok,
    string? Error,
    string Distro,
    bool Healthy,
    string? OsDescription,
    string? Kernel,
    string? User,
    bool HasApt,
    bool HasSudo,
    string? Detail);

/// <summary>Result of <c>run</c>. Exit codes and output are data; refusals set <see cref="Error" />, launch failures set <see cref="ExecutionError" />.</summary>
public sealed record WslRunResult(
    bool Ok,
    string? Error,
    string Distro,
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    bool Capped,
    string? ExecutionError);

/// <summary>Result of <c>debug_setup</c>. <see cref="LaunchConfig" /> is the VS Code launch object for the runtime.</summary>
public sealed record WslDebugSetupResult(
    bool Ok,
    string? Error,
    string Distro,
    string Runtime,
    bool BackendReady,
    string? BackendDetail,
    string? InstallHint,
    string? LaunchConfigName,
    JsonObject? LaunchConfig,
    bool ConfigWritten);
