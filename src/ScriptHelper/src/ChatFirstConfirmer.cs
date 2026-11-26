using AiDevHelpers.Common.Contracts;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// The client never asks a question on its console. A question is refused with the text to ask in chat; the step runs again once the
/// person has said yes there and the command is given its approval flag.
/// </summary>
public sealed class ChatFirstConfirmer : IConfirmer
{
    public bool Confirm(string prompt)
    {
        Console.Error.WriteLine($"needs a yes in chat first: {prompt}");
        return false;
    }
}

/// <summary>Decides whether <c>setup tools</c> may run. It installs the elevated scripts, so it needs the person's yes in chat first.</summary>
public static class SetupApproval
{
    /// <summary>The refusal text when the yes was not given; null when the command may run.</summary>
    public static string? ToolsRefusal(bool approvedInChat) =>
        approvedInChat ? null : "refused: setup tools needs the person's yes in chat first (--approved-in-chat).";
}
