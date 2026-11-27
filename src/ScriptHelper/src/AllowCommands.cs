using System.Text;
using AiDevHelpers.Common;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>Per-script and folder allows, and the repo-wide allow-all. Each is signed and needs an elevated shell.</summary>
public sealed class AllowCommands(SetupDeps deps) : SetupStepBase(deps)
{
    public SetupResult AllowScript(string root, string relativeScript, IReadOnlyList<string> args)
    {
        if (!_elevation.IsElevated())
            return Fail("setup needs an elevated shell");

        var project = _registry.Find(root);
        if (project is null)
            return Fail("project is not registered; run register first");

        var resolved = PathRules.ResolveInsideRoot(project.Root, relativeScript);
        if (!resolved.Ok)
            return Fail($"'{relativeScript}': {resolved.Reason}");

        var rel = Path.GetRelativePath(project.Root, resolved.FullPath).Replace('\\', '/');
        if (ApprovalStore.NormalizeScriptPath(rel) is null)
            return Fail($"'{relativeScript}' is not a valid relative path");

        var fingerprint = FingerprintBuilder.Build(project.Root, rel, _interpreter, _inspector, Allowlist.PinsFor(project.Root, rel));
        if (!fingerprint.Ok)
            return Fail(fingerprint.Reason);

        var path = _config.UserAllowPath(project.Id);
        var loaded = UserAllowStore.Load(path, _signer);
        if (!loaded.Ok)
            return Fail($"existing user allows are not trusted ({loaded.Reason}); refusing to overwrite them");

        if (loaded.Document is not null && !string.Equals(loaded.Document.ProjectId, project.Id, StringComparison.Ordinal))
            return Fail("existing user allows belong to another project; refusing to overwrite them");

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("Allow this script to run elevated for you?");
        sb.AppendLine();
        Section(sb, "Project", project.Root);
        Section(sb, "Script", rel);
        Section(sb, "Arguments", args.Count == 0 ? new[] { "(none)" } : args.ToArray());
        Section(sb, $"Files pinned ({fingerprint.Files.Count})", fingerprint.Files.OrderBy(f => f, StringComparer.Ordinal).ToArray());
        Section(sb, "Fingerprint", fingerprint.Fingerprint);
        sb.AppendLine("The repo is not changed. It is recorded only on this machine, for you, and stops working if the script or its files change.");
        if (!_confirmer.Confirm(sb.ToString()))
            return Fail("allow declined");

        var version = Math.Max(Versions.Get(UserAllowStore.VersionKey(project.Id)), loaded.Document?.Version ?? 0) + 1;
        _ensureDirectory(_config.UserAllowDirectory);
        UserAllowStore.Save(
            path,
            UserAllowStore.With(loaded.Document, project.Id, project.Root, version, new UserAllowEntry(rel, args.ToList(), fingerprint.Fingerprint)),
            _signer);
        Versions.Bump(UserAllowStore.VersionKey(project.Id), version);

        return new SetupResult(true, $"allowed '{rel}' for you (user allows version {version})", project.Id);
    }
    public SetupResult AllowAll(string root) => AllowAllCore(root, approvedHash: null);
    public SetupResult AllowAllApproved(string root, string approvedHash) => AllowAllCore(root, approvedHash);
    private SetupResult AllowAllCore(string root, string? approvedHash)
    {
        if (!_elevation.IsElevated())
            return Fail("setup needs an elevated shell");

        // One command for the whole setup: a repo that is not registered yet is registered here first, in this elevated call.
        // The two allow-all confirmations below still gate the change itself.
        var project = _registry.Find(root);
        if (project is null)
        {
            var registered = new RegistrationCommands(Deps).Register(root);
            if (!registered.Ok)
                return registered;

            project = _registry.Find(root);
            if (project is null)
                return Fail("project was registered but could not be read back");
        }

        var settingsPath = RepoSettingsFile.SettingsPath(project.Root);
        if (!SettingsRequestAllowAll(settingsPath))
            return Fail($"{RepoSettingsFile.FolderName}/{RepoSettingsFile.SettingsFileName} does not request allowAll");

        var hash = RepoSettingsFile.Hash(settingsPath);
        var repo = Path.GetFullPath(project.Root);

        if (approvedHash is not null)
        {
            // The person already answered both questions in chat. Refuse if the settings changed since then.
            if (!string.Equals(hash, approvedHash, StringComparison.OrdinalIgnoreCase))
                return Fail("the repo settings changed since you approved; preview allow-all again");
        }
        else
        {
            if (!_confirmer.Confirm(
                    $"Allow ALL scripts in {repo} to run elevated, without per-script approval?\n" +
                    "Anyone who can change files in this repo, including a teammate's later commit, can then run code as administrator on this machine.\n" +
                    $"Settings hash: {hash}"))
            {
                return Fail("allow-all declined");
            }

            if (!_confirmer.Confirm(
                    $"Second confirmation. Really allow all scripts in {repo} to run elevated on this machine? " +
                    "Editing the repo's settings file will revoke this until you approve again."))
            {
                return Fail("allow-all declined at the second confirmation");
            }
        }

        _ensureDirectory(_config.AllowAllDirectory);
        _ensureDirectory(_config.VersionsDirectory);
        var version = Versions.Get(project.Id) + 1;

        // Signed store first: the tool layer trusts the user pointer, so a pointer must never exist without its store.
        // If the pointer write fails, the new store is removed again, so no approval is left behind without a pointer.
        var storePath = _config.AllowAllPath(project.Id);
        AllowAllStore.Save(storePath, new AllowAllApproval(project.Id, project.Root, version, hash, ""), _signer);
        try
        {
            WriteUserPointer(project, hash);
        }
        catch (Exception)
        {
            File.Delete(storePath);
            throw;
        }

        Versions.Bump(project.Id, version);
        return new SetupResult(true, $"allow-all approved for '{repo}' (version {version})", project.Id);
    }

    /// <summary>
    /// Grants a folder glob (allow-folder). Any script whose full path matches runs elevated without per-script approval, so the two
    /// confirmations name the glob and the risk. The signed grant goes to the admin-only data root; deleting or editing that file revokes it.
    /// </summary>
    public SetupResult AllowFolder(string glob)
    {
        if (!_elevation.IsElevated())
            return Fail("setup needs an elevated shell");

        var normalized = FolderGlob.Normalize(glob);
        if (!FolderGlob.IsAbsolute(normalized))
            return Fail("the folder glob must start with a drive, such as F:\\**\\**");

        if (!_confirmer.Confirm(
                $"Allow ALL scripts whose full path matches {normalized} to run elevated, without per-script approval?\n" +
                "Anyone who can write under this folder, now or later, can then run code as administrator on this machine."))
        {
            return Fail("folder allow declined");
        }

        if (!_confirmer.Confirm(
                $"Second confirmation. Really allow every script matching {normalized} to run elevated, as administrator, on this machine? " +
                "Deleting the grant file under the Script Helper data folder revokes it."))
        {
            return Fail("folder allow declined at the second confirmation");
        }

        FolderAllowStore.Save(_config.FolderAllowDirectory, normalized, _signer, Versions);
        return new SetupResult(true, $"folder allow approved for '{normalized}'");
    }

    private static bool SettingsRequestAllowAll(string settingsPath)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("allowAll", out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // The gitignored per-user file only points at the approval; the approval itself lives in the admin store.
    private static void WriteUserPointer(ProjectEntry project, string hash)
    {
        // Only the allowAll key is replaced; other keys in the file (this user's elevation choices, for one) are kept.
        RepoSettingsFile.UpdateUserFile(project.Root, root => root["allowAll"] = new System.Text.Json.Nodes.JsonObject
        {
            ["projectId"] = project.Id,
            ["settingsHash"] = hash,
        });
    }

    /// <summary>
    /// Checks every file the repo allowlist lists for a valid approval from this machine's admin store. Reads only; use
    /// <see cref="ApproveUnsigned"/> to approve what is missing or changed.
}

