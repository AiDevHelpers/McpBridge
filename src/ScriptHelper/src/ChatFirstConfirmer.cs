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
