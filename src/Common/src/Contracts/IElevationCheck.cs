namespace AiDevHelpers.Common.Contracts;

/// <summary>True only when the process runs with an elevated (UAC) administrator token.</summary>
public interface IElevationCheck
{
    bool IsElevated();
}
