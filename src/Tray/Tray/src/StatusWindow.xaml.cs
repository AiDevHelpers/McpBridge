using System.Windows;

namespace AiDevHelpers.Tray;

/// <summary>The Status page. Closing it hides it, so the tray keeps running and the page opens again from the icon.</summary>
public partial class StatusWindow : Window
{
    /// <summary>The person asked to close the instance on this row. The tray decides, asks, and runs it.</summary>
    public event Action<InstanceRow>? CloseRequested;

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

        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }
}

