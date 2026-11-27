using System.Windows;
using System.Windows.Controls;

namespace AiDevHelpers.Tray;

/// <summary>The Status page. Closing it hides it, so the tray keeps running and the page opens again from the icon.</summary>
public partial class StatusWindow : Window
{
    /// <summary>The person asked to close the instance on this row. The tray decides, asks, and runs it.</summary>
    public event Action<InstanceRow>? CloseRequested;

    /// <summary>The person changed the start setting in the window.</summary>
    public event Action<StartupMode>? StartupChanged;

    private bool _allowClose;

    /// <summary>What the window does at start and when minimized. Set by the tray from the saved settings and by the combo box.</summary>
    public StartupMode Mode { get; private set; } = StartupMode.Open;

    /// <summary>Selects the start setting without raising <see cref="StartupChanged"/>.</summary>
    public void SetMode(StartupMode mode)
    {
        Mode = mode;
        foreach (ComboBoxItem item in StartupBox.Items)
        {
            if (item.Tag is StartupMode m && m == mode)
            {
                StartupBox.SelectedItem = item;
            }
        }
    }

    /// <summary>Lets the window close for real when the tray exits; closing it otherwise only hides it.</summary>
    public void AllowClose() => _allowClose = true;

    private void CloseRow_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is InstanceRow row)
        {
            CloseRequested?.Invoke(row);
        }
    }

    public StatusWindow(StatusViewModel status, InstancesViewModel instances)
    {
        InitializeComponent();
        DataContext = status;
        Title = Strings.Get("AppName");

        StatusTab.Header = Strings.Get("TabStatus");
        InstancesTab.Header = Strings.Get("TabInstances");
        IdeColumn.Header = Strings.Get("ColIde");
        RepoColumn.Header = Strings.Get("ColRepo");
        BranchColumn.Header = Strings.Get("ColBranch");
        PathColumn.Header = Strings.Get("ColPath");
        OpenFileColumn.Header = Strings.Get("ColOpenFile");
        StartedColumn.Header = Strings.Get("ColStarted");
        PidColumn.Header = Strings.Get("ColPid");
        UserColumn.Header = Strings.Get("ColUser");
        ActionColumn.Header = Strings.Get("ColAction");
        EmptyText.Text = Strings.Get("EmptyInstances");

        InstancesGrid.ItemsSource = instances.Rows;
        void ShowEmptyText() => EmptyText.Visibility = instances.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        ShowEmptyText();
        instances.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InstancesViewModel.IsEmpty))
            {
                ShowEmptyText();
            }
        };

        StartupLabel.Text = Strings.Get("StartupLabel");
        StartupBox.Items.Add(new ComboBoxItem { Content = Strings.Get("StartOpen"), Tag = StartupMode.Open });
        StartupBox.Items.Add(new ComboBoxItem { Content = Strings.Get("StartMinimized"), Tag = StartupMode.Minimized });
        StartupBox.Items.Add(new ComboBoxItem { Content = Strings.Get("StartTray"), Tag = StartupMode.Tray });
        StartupBox.SelectionChanged += (_, _) =>
        {
            if (StartupBox.SelectedItem is ComboBoxItem { Tag: StartupMode mode })
            {
                Mode = mode;
                StartupChanged?.Invoke(mode);
            }
        };

        // In tray mode, minimizing hides the window to the tray instead of the taskbar.
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && Mode == StartupMode.Tray)
            {
                WindowState = WindowState.Normal;
                Hide();
            }
        };

        Closing += (_, e) =>
        {
            if (_allowClose)
            {
                return;
            }

            e.Cancel = true;
            Hide();
        };
    }
}

