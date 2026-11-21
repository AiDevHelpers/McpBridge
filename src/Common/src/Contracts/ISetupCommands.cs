namespace AiDevHelpers.Common.Contracts;

/// <summary>The outcome of a setup step. ProjectId is set when a project was registered or approved.</summary>
public sealed record SetupResult(bool Ok, string Reason, string? ProjectId = null);

/// <summary>The elevated Script Helper setup steps: register a project root, approve a script for it.</summary>
public interface ISetupCommands
{
    SetupResult Register(string root);

    SetupResult Approve(string root, string relativeScript);
}
