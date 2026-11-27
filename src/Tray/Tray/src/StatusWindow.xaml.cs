using System.Windows;

namespace AiDevHelpers.Tray;

/// <summary>The Status page. Closing it hides it, so the tray keeps running and the page opens again from the icon.</summary>
public partial class StatusWindow : Window
{
    public StatusWindow(StatusViewModel status, InstancesViewModel instances)
    {
        InitializeComponent();
        DataContext = status;

        StatusTab.Header = Strings.Get("TabStatus");
        InstancesTab.Header = Strings.Get("TabInstances");
        IdeColumn.Header = Strings.Get("ColIde");
        RepoColumn.Header = Strings.Get("ColRepo");
        BranchColumn.Header = Strings.Get("ColBranch");
        PathColumn.Header = Strings.Get("ColPath");
        StartedColumn.Header = Strings.Get("ColStarted");
        PidColumn.Header = Strings.Get("ColPid");
        UserColumn.Header = Strings.Get("ColUser");
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

