using System;
using System.Collections.Generic;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Phase 4 shell services: status bar, activity log, settings read, task list. Each call goes through
/// <see cref="ServiceProvider.GetGlobalService{TService,TInterface}"/> like the other action helpers. Must run on the UI thread.
/// </summary>
internal static class ShellActions
{
    public static object SetStatus(string text, bool confirm)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var change = new { text };
        if (!confirm)
        {
            return new { planned = true, would = change };
        }

        var statusBar = ServiceProvider.GetGlobalService<SVsStatusbar, IVsStatusbar>()
            ?? throw new InvalidOperationException(Strings.Get("StatusBarUnavailable"));
        ErrorHandler.ThrowOnFailure(statusBar.SetText(text));
        return new { planned = false, applied = change };
    }

    public static object WriteActivityLog(string level, string source, string message, bool confirm)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var entry = level switch
        {
            "Error" => __ACTIVITYLOG_ENTRYTYPE.ALE_ERROR,
            "Warning" => __ACTIVITYLOG_ENTRYTYPE.ALE_WARNING,
            "Information" => __ACTIVITYLOG_ENTRYTYPE.ALE_INFORMATION,
            _ => throw new ArgumentException(Strings.Format("UnknownLogLevel", level)),
        };

        var change = new { level, source, message };
        if (!confirm)
        {
            return new { planned = true, would = change };
        }

        var log = ServiceProvider.GetGlobalService<SVsActivityLog, IVsActivityLog>()
            ?? throw new InvalidOperationException(Strings.Get("ActivityLogUnavailable"));
        ErrorHandler.ThrowOnFailure(log.LogEntry((uint)entry, source, message));
        return new { planned = false, applied = change };
    }

    // Shows a text info bar in the main window with plain labels (no click handling; that needs the event channel, phase 5).
    public static object ShowInfoBar(string text, IReadOnlyList<string> actionLabels, bool confirm)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var change = new { text, actions = actionLabels };
        if (!confirm)
        {
            return new { planned = true, would = change };
        }

        var shell = ServiceProvider.GetGlobalService<SVsShell, IVsShell>()
            ?? throw new InvalidOperationException(Strings.Get("ShellUnavailable"));
        ErrorHandler.ThrowOnFailure(shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out var hostObject));
        var host = hostObject as IVsInfoBarHost
            ?? throw new InvalidOperationException(Strings.Get("InfoBarHostUnavailable"));

        var model = new InfoBarModel(text);
        var uiFactory = ServiceProvider.GetGlobalService<SVsInfoBarUIFactory, IVsInfoBarUIFactory>()
            ?? throw new InvalidOperationException(Strings.Get("InfoBarUnavailable"));
        var element = uiFactory.CreateInfoBar(model);
        host.AddInfoBar(element);
        return new { planned = false, applied = change };
    }

    // Reads one VS setting by its path. found is false when the setting does not exist.
    public static object GetSetting(string key)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var reader = ServiceProvider.GetGlobalService<SVsSettingsReader, IVsSettingsReader>()
            ?? throw new InvalidOperationException(Strings.Get("SettingsReaderUnavailable"));
        var hr = reader.ReadSettingString(key, out var value);
        return hr == VSConstants.S_OK ? new { key, value, found = true } : new { key, value = (string?)null, found = false };
    }
}
