using System;
using System.Diagnostics.CodeAnalysis;
using AiDevHelpers.Bridge;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>Writes bridge diagnostics to the IDE Bridge Output pane and in-extension Logs page.</summary>
[SuppressMessage("Usage", "VSTHRD010:Invoke main-thread members", Justification = "BridgeLog buffers calls from any thread and marshals Output window access to the UI thread.")]
public sealed class DebugBridgeLogger : IBridgeLogger
{
    public void LogDebug(string message) => BridgeLog.Write("DEBUG", message);
    public void LogInfo(string message) => BridgeLog.Write("INFO", message);
    public void LogError(string message) => BridgeLog.Write("ERROR", message);
    public void LogException(Exception exception) => BridgeLog.Write("ERROR", exception.ToString());
}
