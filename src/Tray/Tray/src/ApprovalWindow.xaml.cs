using System.Windows;
using System.Windows.Threading;
using AiDevHelpers.MCP.Core.Services;

namespace AiDevHelpers.Tray;

/// <summary>
/// One approval dialog. Only an explicit Allow runs the call. Deny, closing the window, and the request's expiry all refuse it.
/// </summary>
public partial class ApprovalWindow : Window
{
    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _expiry;

    private ApprovalWindow(ApprovalRequest request)
    {
        InitializeComponent();
        Title = Strings.Get("ApprovalTitle");
        PromptText.Text = Strings.Get("ApprovalPrompt");
        DetailText.Text = Strings.Get("ApprovalDetail");
        DenyButton.Content = Strings.Get("Deny");
        AllowButton.Content = Strings.Get("AllowOnce");
        ToolText.Text = request.ToolName;
        AllowButton.Click += (_, _) => Finish(true);
        DenyButton.Click += (_, _) => Finish(false);
        Closed += (_, _) => _answer.TrySetResult(false);

        var remaining = request.ExpiresUtc - DateTime.UtcNow;
        _expiry = new DispatcherTimer { Interval = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1) };
        _expiry.Tick += (_, _) => Finish(false);
    }

    /// <summary>Shows the dialog and completes with the person's answer. Call on the UI thread.</summary>
    public static Task<bool> Ask(ApprovalRequest request)
    {
        var window = new ApprovalWindow(request);
        window.Show();
        window.Activate();
        window._expiry.Start();
        return window._answer.Task;
    }

    private void Finish(bool allowed)
    {
        _expiry.Stop();
        _answer.TrySetResult(allowed);
        Close();
    }
}

