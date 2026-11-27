using System.Windows;
using System.Windows.Controls;

namespace AiDevHelpers.Tray;

/// <summary>A yes-or-no question for one action on an instance. Deny is the default; closing the window is a deny.</summary>
public static class ConfirmDialog
{
    public static Task<bool> Ask(string title, string text)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
        };

        var yes = new Button { Width = 100, Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Width = 100, IsDefault = true, IsCancel = true };
        yes.Click += (_, _) =>
        {
            answer.TrySetResult(true);
            window.Close();
        };
        no.Click += (_, _) => window.Close();
        window.Closed += (_, _) => answer.TrySetResult(false);

        window.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 16, 0, 0),
                    Children = { yes, no },
                },
            },
        };
        yes.Content = Strings.Get("CloseButton");
        no.Content = Strings.Get("Deny");

        window.Show();
        window.Activate();
        return answer.Task;
    }
}
