namespace AiDevHelpers.MCP.Core.Services;

/// <summary>Settings the IDE tools read. The bridge MCP binds this from its own configuration.</summary>
public sealed class IdeToolsOptions
{
    /// <summary>"DotnetTest" or "TestExplorer". Empty means TestExplorer.</summary>
    public string IdeTestRunMode { get; set; } = "";

    /// <summary>Global only. When true, the bridge answers dialogs with the IDE's defaults during an action (SuppressUI).</summary>
    public bool AutoDismissDialogs { get; set; } = true;

    /// <summary>Global only. Seconds an IDE action may wait on the UI thread before the bridge replies with an error and reports a dialog.</summary>
    public int ActionTimeoutSeconds { get; set; } = 60;
}
