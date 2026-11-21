namespace AiDevHelpers.Common.Contracts;

/// <summary>Asks a person to confirm an action. Must return false when no person can answer.</summary>
public interface IConfirmer
{
    bool Confirm(string prompt);
}
