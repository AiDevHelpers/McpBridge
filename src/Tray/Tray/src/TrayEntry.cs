using System.IO;
using System.Windows;
using AiDevHelpers.Tray;

namespace AiDevHelpers.Tray;

/// <summary>Starts the tray: one instance per user, a crash-loop check, then the WPF message loop.</summary>
public static class TrayEntry
{
    private const string MutexName = @"Local\AiDevHelpers.Tray";

    [STAThread]
    public static int Run()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        if (!first)
        {
            // Another tray is already running for this user; it owns the icon and the approval pipe.
            return 0;
        }

        var crashes = new CrashLoopGuard(CrashFile(), maxCrashes: 3, window: TimeSpan.FromMinutes(5));
        if (crashes.IsTripped(DateTime.UtcNow))
        {
            MessageBox.Show(
                "AiDevHelpers tray stopped after repeated crashes, so it will not start again yet. Fix the cause, then delete the crashes file in the AiDevHelpers tray folder.",
                "AiDevHelpers",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return 1;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            crashes.RecordCrash(DateTime.UtcNow);
            e.Handled = false;
        };

        // The tray is built in Startup: WPF installs the UI thread's SynchronizationContext when Run starts, and the view model needs it.
        TrayApp? tray = null;
        app.Startup += (_, _) =>
        {
            tray = new TrayApp();
            _ = tray.RunApprovalsAsync();
        };
        app.Exit += (_, _) => tray?.Dispose();
        return app.Run();
    }

    private static string CrashFile()
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.GetTempPath();
        return Path.Combine(local, "AiDevHelpers", "tray", "crashes.txt");
    }
}

