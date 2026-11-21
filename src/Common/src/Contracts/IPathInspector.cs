namespace AiDevHelpers.Common.Contracts;

/// <summary>Inspects paths on disk, so file system checks can be replaced in tests.</summary>
public interface IPathInspector
{
    /// <summary>True when the path is a reparse point (junction or symlink).</summary>
    bool IsReparsePoint(string path);
}
