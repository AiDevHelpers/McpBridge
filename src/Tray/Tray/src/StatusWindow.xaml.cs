using System.Windows;

namespace AiDevHelpers.Tray;

/// <summary>The Status page. Closing it hides it, so the tray keeps running and the page opens again from the icon.</summary>
public partial class StatusWindow : Window
{
    public StatusWindow(StatusViewModel status)
    {
        InitializeComponent();
        DataContext = status;
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }
}

