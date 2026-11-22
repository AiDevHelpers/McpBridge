using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Writes to a named Output window pane (outputWrite). The pane is created on first use and found again by a
/// GUID derived from its name, so the same channel name always lands in the same pane. Must run on the UI thread.
/// </summary>
internal static class OutputChannelActions
{
    public static object Write(string channel, string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var output = ServiceProvider.GetGlobalService<SVsOutputWindow, IVsOutputWindow>()
            ?? throw new InvalidOperationException(Strings.Get("OutputWindowUnavailable"));

        var paneGuid = PaneGuid(channel);
        if (output.GetPane(ref paneGuid, out var pane) != VSConstants.S_OK || pane is null)
        {
            ErrorHandler.ThrowOnFailure(output.CreatePane(ref paneGuid, channel, 1, 0));
            ErrorHandler.ThrowOnFailure(output.GetPane(ref paneGuid, out pane));
        }

        ErrorHandler.ThrowOnFailure(pane.OutputStringThreadSafe(text.EndsWith("\n", StringComparison.Ordinal) ? text : text + "\n"));
        return new { channel, written = text.Length };
    }

    /// <summary>A stable GUID per channel name: the MD5 of the name, which is 16 bytes.</summary>
    internal static Guid PaneGuid(string channel)
    {
        using var md5 = MD5.Create();
        return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes("AiDevHelpers.Output:" + channel)));
    }
}
