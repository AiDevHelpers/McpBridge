namespace AiDevHelpers.Common.Contracts;

/// <summary>Identifies the interpreter that will run a script, so an interpreter change needs re-approval.</summary>
public interface IInterpreterInfo
{
    string Describe();
}
