using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AiDevHelpers.Common;
using AiDevHelpers.Common.Processes;
using AiDevHelpers.Helpers.PackageHelper;

namespace AiDevHelpers.Helpers.ProjectHelper;

public enum FixKind
{
    SetProperty,
    AddPackage,
    SetPackageVersion,
    Retarget,
}

/// <summary>One edit the fix would make. <see cref="Id"/> is <c>Kind:Project:Name</c>, the key a skip list uses.</summary>
public sealed record FixAction(string Id, string Project, FixKind Kind, string Name, string? Value, string Description);

/// <summary>The planned edits and the token that confirms them. <see cref="Token"/> is null when there is nothing to fix.</summary>
public sealed record FixPlanResult(string? Token, IReadOnlyList<FixAction> Actions, IReadOnlyList<string> Notes);

/// <summary>The outcome of a confirm. On failure every file is back to its original bytes.</summary>
public sealed record FixConfirmResult(bool Ok, string? Error, IReadOnlyList<string> Changed, string? BuildOutput);

/// <summary>
/// Plans and applies edits that bring projects to the standards. Plan writes nothing; confirm re-checks the plan, applies the
/// edits, re-audits and builds the changed projects, and restores every file when any step fails.
/// </summary>
public static class ProjectFixer
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private const string TestSdk = "Microsoft.NET.Test.Sdk";
    private static readonly ConcurrentDictionary<string, Pending> Tokens = new();

    private sealed record Pending(string Root, string? Filter, IReadOnlyList<string> Skip, IReadOnlyList<FixAction> Actions, IReadOnlyDictionary<string, string> Hashes, DateTime Expires);

    public static FixPlanResult Plan(string root, string? projectFilter, IReadOnlyCollection<string> skip)
    {
        var (actions, notes, standardsPresent) = PlanCore(root, projectFilter, skip);
        if (!standardsPresent || actions.Count == 0)
        {
            return new FixPlanResult(null, actions, notes);
        }

        var hashes = actions.Select(a => a.Project).Distinct(StringComparer.Ordinal)
            .ToDictionary(p => p, p => Hash(File.ReadAllBytes(FullPath(root, p))), StringComparer.Ordinal);
        var token = Guid.NewGuid().ToString("N");
        Tokens[token] = new Pending(Path.GetFullPath(root), projectFilter, skip.ToList(), actions, hashes, DateTime.UtcNow + Lifetime);
        return new FixPlanResult(token, actions, notes);
    }

    public static async Task<FixConfirmResult> ConfirmAsync(string root, string token, RunCommand run, CancellationToken ct)
    {
        if (!Tokens.TryRemove(token, out var pending) || pending.Root != Path.GetFullPath(root))
        {
            return Fail(Strings.Get("FixTokenUnknown"));
        }

        if (DateTime.UtcNow > pending.Expires)
        {
            return Fail(Strings.Get("FixTokenExpired"));
        }

        var (fresh, _, _) = PlanCore(root, pending.Filter, pending.Skip);
        if (!fresh.Select(a => a.Id).OrderBy(i => i, StringComparer.Ordinal).SequenceEqual(pending.Actions.Select(a => a.Id).OrderBy(i => i, StringComparer.Ordinal)))
        {
            return Fail(Strings.Get("FixDrift"));
        }

        foreach (var (project, hash) in pending.Hashes)
        {
            if (Hash(File.ReadAllBytes(FullPath(root, project))) != hash)
            {
                return Fail(Strings.Get("FixDrift"));
            }
        }

        var affected = pending.Hashes.Keys.ToList();
        var originals = affected.ToDictionary(p => p, p => File.ReadAllBytes(FullPath(root, p)), StringComparer.Ordinal);
        try
        {
            foreach (var project in affected)
            {
                var actions = pending.Actions.Where(a => a.Project == project).ToList();
                File.WriteAllBytes(FullPath(root, project), ApplyTo(originals[project], actions));
            }

            var standards = ProjectStandardsFile.Read(root).Standards;
            var audit = ProjectAudit.Run(root, standards, projectFilter: null);
            var remaining = audit.Findings.Where(f => f.Severity == "error" && affected.Contains(f.Project)).ToList();
            if (remaining.Count > 0)
            {
                Restore(root, originals);
                return Fail(Strings.Format("FixAuditRemaining", string.Join("; ", remaining.Select(f => $"{f.Project}: {f.Message}"))));
            }

            foreach (var project in affected)
            {
                var result = await run(new CommandRequest
                {
                    FileName = "dotnet",
                    Arguments = ["build", FullPath(root, project), "-warnaserror", "--nologo"],
                    WorkingDirectory = root,
                    Timeout = TimeSpan.FromMinutes(10),
                }, ct);

                if (!result.Success)
                {
                    Restore(root, originals);
                    var output = Tail(string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr);
                    return new FixConfirmResult(false, Strings.Format("FixBuildFailed", project), [], output);
                }
            }

            return new FixConfirmResult(true, null, affected, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            Restore(root, originals);
            return Fail(Strings.Format("FixFailed", ex.Message));
        }
    }

    private static (List<FixAction> Actions, List<string> Notes, bool StandardsPresent) PlanCore(string root, string? projectFilter, IReadOnlyCollection<string> skip)
    {
        var actions = new List<FixAction>();
        var notes = new List<string>();
        var standards = ProjectStandardsFile.Read(root).Standards;
        if (standards is null)
        {
            notes.Add(Strings.Format("FixNoStandards", ProjectStandardsFile.FilePath(root)));
            return (actions, notes, false);
        }

        var configPath = ProjectStandardsFile.FilePath(root);
        var all = ProjectFiles.Read(root);
        var allUses = all.SelectMany(p => p.Uses).ToList();
        var scope = all.Where(p => projectFilter is null || string.Equals(p.Path, ElevationFiles.Normalize(projectFilter), StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var project in scope)
        {
            foreach (var result in FrameworkGuard.Check(project.Path, project.Frameworks, standards, configPath).Where(r => r.Status == GuardStatus.Refused))
            {
                actions.Add(Action(FixKind.Retarget, project.Path, result.Framework, standards.BaseFramework));
            }

            foreach (var (name, expected) in standards.Properties)
            {
                if (!project.Properties.TryGetValue(name, out var found) || !string.Equals(found, expected, StringComparison.OrdinalIgnoreCase))
                {
                    actions.Add(Action(FixKind.SetProperty, project.Path, name, expected));
                }
            }

            foreach (var analyzer in standards.Analyzers.Where(a => !project.Uses.Any(u => string.Equals(u.Package, a, StringComparison.OrdinalIgnoreCase))))
            {
                AddWithChosenVersion(actions, notes, allUses, project.Path, analyzer);
            }

            if (IsTest(project.Name) && !project.Uses.Any(u => string.Equals(u.Package, TestSdk, StringComparison.OrdinalIgnoreCase)))
            {
                AddWithChosenVersion(actions, notes, allUses, project.Path, TestSdk);
            }

            foreach (var use in project.Uses.Where(u => u.Version is not null && !u.Version.Contains('*')))
            {
                var choice = PackageVersions.Choose(allUses.Where(u => string.Equals(u.Package, use.Package, StringComparison.OrdinalIgnoreCase)).ToList());
                if (choice.Version is not null && choice.Version != use.Version)
                {
                    actions.Add(Action(FixKind.SetPackageVersion, project.Path, use.Package, choice.Version));
                }
            }
        }

        var kept = actions.Where(a => !skip.Contains(a.Id, StringComparer.OrdinalIgnoreCase)).DistinctBy(a => a.Id).ToList();
        return (kept, notes, true);
    }

    private static void AddWithChosenVersion(List<FixAction> actions, List<string> notes, List<PackageUse> allUses, string project, string package)
    {
        var choice = PackageVersions.Choose(allUses.Where(u => string.Equals(u.Package, package, StringComparison.OrdinalIgnoreCase)).ToList());
        if (choice.Version is null)
        {
            notes.Add(Strings.Format("FixNoVersion", package));
            return;
        }

        actions.Add(Action(FixKind.AddPackage, project, package, choice.Version));
    }

    private static FixAction Action(FixKind kind, string project, string name, string value)
    {
        var id = $"{kind}:{project}:{name}";
        var text = kind switch
        {
            FixKind.SetProperty => Strings.Format("FixDescSetProperty", name, value, project),
            FixKind.AddPackage => Strings.Format("FixDescAddPackage", name, value, project),
            FixKind.SetPackageVersion => Strings.Format("FixDescSetVersion", name, value, project),
            _ => Strings.Format("FixDescRetarget", project, name, value),
        };
        return new FixAction(id, project, kind, name, value, text);
    }

    private static bool IsTest(string name) =>
        name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase);

    /// <summary>Applies the actions to one project file's bytes, keeping its line endings and byte order mark.</summary>
    public static byte[] ApplyTo(byte[] original, IReadOnlyList<FixAction> actions)
    {
        var hasBom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(original, hasBom ? 3 : 0, original.Length - (hasBom ? 3 : 0));
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new XmlException(Strings.Get("FixNoRootElement"));
        foreach (var action in actions)
        {
            Apply(root, action, newline);
        }

        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = doc.Declaration is null, Indent = false };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            doc.Save(writer);
        }

        var output = new UTF8Encoding(false).GetString(stream.ToArray()).Replace("\r\n", "\n").Replace("\n", newline);
        var bytes = new UTF8Encoding(false).GetBytes(output);
        return hasBom ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes;
    }

    private static void Apply(XElement root, FixAction action, string newline)
    {
        var ns = root.Name.Namespace;
        switch (action.Kind)
        {
            case FixKind.SetProperty:
            {
                var existing = root.Elements().Where(e => e.Name.LocalName == "PropertyGroup" && e.Attribute("Condition") is null)
                    .SelectMany(g => g.Elements()).FirstOrDefault(e => e.Name.LocalName == action.Name);
                if (existing is not null)
                {
                    existing.Value = action.Value!;
                    return;
                }

                var group = root.Elements().FirstOrDefault(e => e.Name.LocalName == "PropertyGroup" && e.Attribute("Condition") is null)
                    ?? AppendChild(root, new XElement(ns + "PropertyGroup"), newline);
                AppendChild(group, new XElement(ns + action.Name, action.Value), newline);
                return;
            }

            case FixKind.AddPackage:
            {
                var element = new XElement(ns + "PackageReference", new XAttribute("Include", action.Name), new XAttribute("Version", action.Value!));
                var group = root.Elements().FirstOrDefault(e => e.Name.LocalName == "ItemGroup" && e.Attribute("Condition") is null
                    && e.Elements().Any(i => i.Name.LocalName == "PackageReference"))
                    ?? AppendChild(root, new XElement(ns + "ItemGroup"), newline);
                AppendChild(group, element, newline);
                return;
            }

            case FixKind.SetPackageVersion:
            {
                foreach (var item in root.Descendants().Where(e => e.Name.LocalName == "PackageReference"
                    && (string.Equals(e.Attribute("Include")?.Value, action.Name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(e.Attribute("Update")?.Value, action.Name, StringComparison.OrdinalIgnoreCase))))
                {
                    if (item.Attribute("Version") is { } attribute)
                    {
                        attribute.Value = action.Value!;
                    }
                    else if (item.Elements().FirstOrDefault(e => e.Name.LocalName == "Version") is { } child)
                    {
                        child.Value = action.Value!;
                    }
                }

                return;
            }

            default:
            {
                foreach (var element in root.Descendants().Where(e => e.Name.LocalName == "TargetFramework").ToList())
                {
                    if (element.Value.Trim() == action.Name)
                    {
                        element.Value = action.Value!;
                    }
                }

                foreach (var element in root.Descendants().Where(e => e.Name.LocalName == "TargetFrameworks").ToList())
                {
                    var list = element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    if (!list.Contains(action.Name, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    list = list.Select(f => f == action.Name ? action.Value! : f).Distinct(StringComparer.Ordinal).ToList();
                    element.Value = string.Join(';', list);
                }

                return;
            }
        }
    }

    /// <summary>Adds a child element on its own line, indented like its siblings, and returns it.</summary>
    public static XElement AppendChild(XElement parent, XElement child, string newline)
    {
        var parentIndent = IndentOf(parent.PreviousNode as XText);
        var childIndent = parent.Elements().FirstOrDefault() is { } first
            ? IndentOf(first.PreviousNode as XText)
            : parentIndent + "  ";
        var closing = parent.LastNode as XText;
        if (closing is not null && string.IsNullOrWhiteSpace(closing.Value))
        {
            closing.AddBeforeSelf(new XText(newline + childIndent), child);
        }
        else
        {
            parent.Add(new XText(newline + childIndent), child, new XText(newline + parentIndent));
        }

        return child;
    }

    /// <summary>The whitespace after the last line break of a text node, or an empty string.</summary>
    private static string IndentOf(XText? text)
    {
        if (text is null)
        {
            return string.Empty;
        }

        var value = text.Value;
        var lastBreak = value.LastIndexOf('\n');
        return lastBreak < 0 ? string.Empty : new string(value[(lastBreak + 1)..].TakeWhile(c => c == ' ' || c == '\t').ToArray());
    }

    private static void Restore(string root, IReadOnlyDictionary<string, byte[]> originals)
    {
        foreach (var (project, bytes) in originals)
        {
            File.WriteAllBytes(FullPath(root, project), bytes);
        }
    }

    private static string FullPath(string root, string project) =>
        Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string Tail(string text) => text.Length <= 2000 ? text : text[^2000..];

    private static FixConfirmResult Fail(string error) => new(false, error, [], null);
}
