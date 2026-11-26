using System;

namespace AiDevHelpers.Bridge;

/// <summary>
/// Logging seam for the bridge. The host adapts its own logger (for the host, AiDevHelpers.Logger) to this interface.
/// </summary>
public interface IBridgeLogger
{
    /// <summary>Writes a debug-level message.</summary>
    void LogDebug(string message);

    /// <summary>Writes an informational message.</summary>
    void LogInfo(string message);

    /// <summary>Writes an error message.</summary>
    void LogError(string message);

    /// <summary>Writes an exception with its details.</summary>
    void LogException(Exception exception);
}
