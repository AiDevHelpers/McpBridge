using System.Text.Json;

namespace AiDevHelpers.ScriptHelper.Core;

public sealed record ProjectEntry(string Id, string Root);

public sealed record RegistryDocument(IReadOnlyList<ProjectEntry> Projects);

/// <summary>
/// The list of project roots the broker may run scripts for. Holds roots only, no hashes.
/// Registering needs an elevated shell, an existing directory on NTFS, and a root that is not
/// a drive root or the user profile (or a folder containing it).
/// </summary>
public sealed class ProjectRegistry
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly IElevationCheck _elevation;
    private readonly IVolumeInfo _volumes;
    private readonly string _profileRoot;
    private readonly Action<string> _ensureDirectory;

    public ProjectRegistry(
        string registryPath,
        IElevationCheck elevation,
        IVolumeInfo volumes,
        string userProfileRoot,
        Action<string>? ensureDirectory = null)
    {
        _path = registryPath;
        _elevation = elevation;
        _volumes = volumes;
        _profileRoot = Normalize(userProfileRoot);
        _ensureDirectory = ensureDirectory ?? (p => Directory.CreateDirectory(p));
    }

    public SetupResult Register(string root)
    {
        if (!_elevation.IsElevated())
            return Fail("setup needs an elevated shell");

        string full;
        bool isDriveRoot;
        try
        {
            var raw = Path.GetFullPath(root);
            isDriveRoot = string.Equals(Path.GetPathRoot(raw), raw, StringComparison.OrdinalIgnoreCase);
            full = raw.TrimEnd('\\');
        }
        catch (ArgumentException)
        {
            return Fail("project root is not a valid path");
        }

        if (isDriveRoot)
            return Fail("refusing a drive root");

        if (string.Equals(full, _profileRoot, StringComparison.OrdinalIgnoreCase)
            || _profileRoot.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("refusing a user-profile root or a folder that contains it");
        }

        if (!Directory.Exists(full))
            return Fail("project root does not exist");

        if (!string.Equals(_volumes.FileSystem(full), "NTFS", StringComparison.OrdinalIgnoreCase))
            return Fail("project root must be on an NTFS volume");

        if (!TryLoad(out var projects, out var loadReason))
            return Fail(loadReason);

        var existing = projects.FirstOrDefault(p => string.Equals(p.Root, full, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return new SetupResult(true, "already registered", existing.Id);

        var entry = new ProjectEntry(Guid.NewGuid().ToString("N"), full);
        projects.Add(entry);
        Save(projects);

        return new SetupResult(true, "registered", entry.Id);
    }

    public ProjectEntry? Find(string root)
    {
        if (!TryLoad(out var projects, out _))
            return null;

        string full;
        try
        {
            full = Normalize(root);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return projects.FirstOrDefault(p => string.Equals(p.Root, full, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string root) => Path.GetFullPath(root).TrimEnd('\\');

    private bool TryLoad(out List<ProjectEntry> projects, out string reason)
    {
        projects = new List<ProjectEntry>();
        reason = string.Empty;

        if (!File.Exists(_path))
            return true;

        try
        {
            var doc = JsonSerializer.Deserialize<RegistryDocument>(File.ReadAllText(_path));
            if (doc?.Projects is null || doc.Projects.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Root)))
            {
                reason = "registry file is malformed";
                return false;
            }

            projects = doc.Projects.ToList();
            return true;
        }
        catch (JsonException)
        {
            reason = "registry file is malformed";
            return false;
        }
        catch (IOException)
        {
            reason = "registry file cannot be read";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            reason = "registry file cannot be read";
            return false;
        }
    }

    private void Save(List<ProjectEntry> projects)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        _ensureDirectory(dir);

        var temp = Path.Combine(dir, Path.GetRandomFileName());
        File.WriteAllText(temp, JsonSerializer.Serialize(new RegistryDocument(projects), Options));
        File.Move(temp, _path, overwrite: true);
    }

    private static SetupResult Fail(string reason) => new(false, reason);
}
