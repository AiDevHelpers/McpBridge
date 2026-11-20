using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>Buffers bridge diagnostics for the Logs page and writes them to a VS Output pane (MCP vsout channel).</summary>
[SuppressMessage("Usage", "VSTHRD001:Use SwitchToMainThreadAsync", Justification = "BeginInvoke posts this non-blocking log write to the UI thread without synchronously waiting.")]
[SuppressMessage("Usage", "VSTHRD010:Invoke main-thread members", Justification = "WriteToOutputPane is called directly only on the UI thread or from its posted callback.")]
public static class BridgeLog
{
    private static readonly ConcurrentQueue<string> Entries = new();
    private static readonly Guid OutputPaneGuid = new("A3F2C4B1-6D7E-4F80-9A1B-2C3D4E5F6071");
    private const string OutputPaneTitle = "AiDevHelpers IDE Bridge";
    private const int MaxEntries = 1000;

    public static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Entries.Enqueue(line);
        while (Entries.Count > MaxEntries)
        {
            Entries.TryDequeue(out _);
        }

        if (ThreadHelper.CheckAccess())
        {
            WriteToOutputPane(line);
        }
        else
        {
            ThreadHelper.Generic.BeginInvoke(new Action(() => WriteToOutputPane(line)));
        }
    }

    public static string ReadAll() => string.Join(Environment.NewLine, Entries.ToArray());

    private static void WriteToOutputPane(string line)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var output = ServiceProvider.GetGlobalService<SVsOutputWindow, IVsOutputWindow>();
        if (output is null)
        {
            return;
        }

        var paneGuid = OutputPaneGuid;
        if (output.GetPane(ref paneGuid, out var pane) != VSConstants.S_OK || pane is null)
        {
            output.CreatePane(ref paneGuid, OutputPaneTitle, 1, 1);
            output.GetPane(ref paneGuid, out pane);
        }

        pane?.OutputStringThreadSafe(line + Environment.NewLine);
    }
}
