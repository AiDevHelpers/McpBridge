using AiDevHelpers.Installers.Vs2026.Core;
using AiDevHelpers.Bridge;
using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;
using System.Diagnostics.CodeAnalysis;

namespace AiDevHelpers.Installers.Vs2026.Installer;

/// <summary>Settings, Services, and Logs pages opened from the Visual Studio Extensions menu.</summary>
[SuppressMessage("Usage", "VSTHRD010:Invoke main-thread members", Justification = "The package creates and uses this tool window on the Visual Studio UI thread.")]
public sealed class BridgeToolWindow : ToolWindowPane
{
    public const int SettingsPage = 0;
    public const int ServicesPage = 1;
    public const int LogsPage = 2;

    private readonly TabControl _tabs;
    private readonly Label _serviceStatus;
    private readonly TextBox _logs;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    public BridgeToolWindow() : base(null)
    {
        Caption = "AiDevHelpers IDE Bridge";
        _tabs = new TabControl { Dock = DockStyle.Fill };
        _serviceStatus = new Label { AutoSize = true, Padding = new Padding(4), Text = BridgeControl.Describe() };
        _logs = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9),
        };

        _tabs.TabPages.Add(BuildSettingsPage());
        _tabs.TabPages.Add(BuildServicesPage());
        _tabs.TabPages.Add(BuildLogsPage());
        Content = _tabs;

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000, Enabled = true };
        _refreshTimer.Tick += (_, _) => RefreshPage();
        _tabs.Disposed += (_, _) => _refreshTimer.Dispose();
    }

    public void SelectPage(int page)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (page >= 0 && page < _tabs.TabPages.Count)
        {
            _tabs.SelectedIndex = page;
        }

        RefreshPage();
    }

    private TabPage BuildSettingsPage()
    {
        var page = new TabPage("Settings");
        page.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            Text = "The IDE bridge starts automatically when Visual Studio opens.\r\n" +
                   "It uses a per-process named pipe and registers this instance for MCP routing.\r\n\r\n" +
                   "There are no user-configurable bridge settings at this time.",
        });
        return page;
    }

    private TabPage BuildServicesPage()
    {
        var page = new TabPage("Services");
        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        panel.Controls.Add(_serviceStatus);
        panel.SetFlowBreak(_serviceStatus, true);
        panel.Controls.Add(MakeButton("Start", () => BridgeControl.Start()));
        panel.Controls.Add(MakeButton("Stop", () => BridgeControl.Stop()));
        panel.Controls.Add(MakeButton("Refresh", () => BridgeControl.Describe()));
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildLogsPage()
    {
        var page = new TabPage("Logs");
        page.Controls.Add(_logs);
        return page;
    }

    private static Button MakeButton(string text, Func<string> action)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(4) };
        button.Click += (_, _) =>
        {
            string result = action();
            BridgeLog.Write("INFO", result);
        };
        return button;
    }

    private void RefreshPage()
    {
        _serviceStatus.Text = BridgeControl.Describe();
        string text = BridgeLog.ReadAll();
        if (!string.Equals(_logs.Text, text, StringComparison.Ordinal))
        {
            _logs.Text = text;
            _logs.SelectionStart = _logs.TextLength;
            _logs.ScrollToCaret();
        }
    }
}
