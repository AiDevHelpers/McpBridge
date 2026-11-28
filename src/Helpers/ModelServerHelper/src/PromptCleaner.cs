using System.Text;

namespace AiDevHelpers.Helpers.ModelServerHelper;

/// <summary>Prompt cleanup for clean_and_chat: trims the text and removes control characters, keeping tabs and line breaks.</summary>
public static class PromptCleaner
{
    public static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsControl(c) || c is '\t' or '\r' or '\n')
                sb.Append(c);
        }

        return sb.ToString().Trim();
    }
}
