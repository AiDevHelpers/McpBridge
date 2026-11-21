namespace AiDevHelpers.Common.Contracts;

/// <summary>Reports the file system name (for example NTFS) of the volume holding a path; null when unknown.</summary>
public interface IVolumeInfo
{
    string? FileSystem(string path);
}
