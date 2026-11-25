using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using AiDevHelpers.Common;
using AiDevHelpers.Common.Processes;
using AiDevHelpers.Helpers.PackageHelper;

namespace AiDevHelpers.Helpers.ProjectHelper;

/// <summary>What to add. <paramref name="Folder"/> and <paramref name="SolutionFolder"/> are optional; <paramref name="Folder"/> defaults to the solution's inferred layout.</summary>
public sealed record AddRequest(
    string Root,
    string Solution,
    string Template,
    string Name,
    string? Folder,
    string? SolutionFolder,
    string Language,
    string? Framework,
    IReadOnlyList<string> Packages,
    string? ProjectReference = null,
    bool SeedTest = false);

/// <summary>The plan: what will be created, edited and removed. <see cref="Token"/> is null when there are errors.</summary>
public sealed record AddPlanResult(
    string? Token,
    string? ProjectPath,
    string? Framework,
    IReadOnlyList<FixAction> Edits,
    IReadOnlyList<string> Stubs,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Notes);

public sealed record AddConfirmResult(bool Ok, string? Error, IReadOnlyList<string> Changed, string? BuildOutput);

/// <summary>
/// Adds a project of any template to the solution, set up to the standards. Plan writes nothing. Confirm re-checks the plan, creates
/// the project, removes the template stubs, applies the edits, lists the project in the solution, and builds it. A failed build
/// removes what was created and restores the solution byte for byte.
/// </summary>
public static class ProjectAdder
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private const string TestSdk = "Microsoft.NET.Test.Sdk";
    private static readonly string[] TemplateStubs = ["Class1.cs", "UnitTest1.cs", "Class1.fs", "UnitTest1.fs", "Class1.vb"];
    private static readonly Regex NamePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, Pending> Tokens = new();

    private sealed record Pending(AddRequest Request, string ProjectPath, IReadOnlyList<string> EditIds, string SolutionHash, DateTime Expires);

    public static async Task<AddPlanResult> PlanAsync(AddRequest request, RunCommand run, CancellationToken ct)
    {
        var plan = await BuildAsync(request, run, ct);
        if (plan.Errors.Count > 0 || plan.ProjectPath is null)
        {
            return plan;
        }

        var token = Guid.NewGuid().ToString("N");
        Tokens[token] = new Pending(
            request,
            plan.ProjectPath,
            plan.Edits.Select(a => a.Id).ToList(),
            Hash(File.ReadAllBytes(SolutionFull(request))),
            DateTime.UtcNow + Lifetime);
        return plan with { Token = token };
    }

    public static async Task<AddConfirmResult> ConfirmAsync(string root, string token, RunCommand run, CancellationToken ct)
    {
        if (!Tokens.TryRemove(token, out var pending) || Path.GetFullPath(pending.Request.Root) != Path.GetFullPath(root))
        {
            return Fail(Strings.Get("FixTokenUnknown"));
        }

        if (DateTime.UtcNow > pending.Expires)
        {
            return Fail(Strings.Get("FixTokenExpired"));
        }

        var request = pending.Request;
        var again = await BuildAsync(request, run, ct);
        if (again.Errors.Count > 0)
        {
            return Fail(string.Join(" ", again.Errors));
        }

        if (again.ProjectPath != pending.ProjectPath
            || !again.Edits.Select(a => a.Id).SequenceEqual(pending.EditIds)
            || Hash(File.ReadAllBytes(SolutionFull(request))) != pending.SolutionHash)
        {
            return Fail(Strings.Get("FixDrift"));
        }

        var solutionFile = SolutionFull(request);
        var solutionBytes = File.ReadAllBytes(solutionFile);
        var folder = FolderFull(request.Root, pending.ProjectPath);
        var folderExisted = Directory.Exists(folder);
        var projectFile = Path.Combine(request.Root, pending.ProjectPath.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            Directory.CreateDirectory(folder);
            var created = await run(new CommandRequest
            {
                FileName = "dotnet",
                Arguments = ["new", request.Template, "-n", request.Name, "-o", folder, "--language", request.Language, "-f", again.Framework!],
                WorkingDirectory = request.Root,
                Timeout = TimeSpan.FromMinutes(5),
            }, ct);
            if (!created.Success)
            {
                Rollback(folder, folderExisted, solutionFile, solutionBytes);
                return new AddConfirmResult(false, Strings.Format("FixFailed", Tail(created.StdErr.Length > 0 ? created.StdErr : created.StdOut)), [], null);
            }

            foreach (var stub in TemplateStubs.Select(s => Path.Combine(folder, s)).Where(File.Exists))
            {
                File.Delete(stub);
            }

            if (request.SeedTest)
            {
                File.WriteAllText(Path.Combine(folder, "SeedTest.cs"), Strings.Format("SeedTestSource", request.Name));
            }

            File.WriteAllBytes(projectFile, ProjectFixer.ApplyTo(File.ReadAllBytes(projectFile), again.Edits));
            File.WriteAllBytes(solutionFile, SlnxWriter.Insert(solutionBytes, request.SolutionFolder, pending.ProjectPath));

            var build = await run(new CommandRequest
            {
                FileName = "dotnet",
                Arguments = ["build", projectFile, "-warnaserror", "--nologo"],
                WorkingDirectory = request.Root,
                Timeout = TimeSpan.FromMinutes(10),
            }, ct);
            if (!build.Success)
            {
                Rollback(folder, folderExisted, solutionFile, solutionBytes);
                return new AddConfirmResult(false, Strings.Format("FixBuildFailed", pending.ProjectPath), [], Tail(string.IsNullOrWhiteSpace(build.StdErr) ? build.StdOut : build.StdErr));
            }

            return new AddConfirmResult(true, null, [pending.ProjectPath, Path.GetFileName(solutionFile)], null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidOperationException)
        {
            Rollback(folder, folderExisted, solutionFile, solutionBytes);
            return Fail(Strings.Format("FixFailed", ex.Message));
        }
    }

    private static async Task<AddPlanResult> BuildAsync(AddRequest request, RunCommand run, CancellationToken ct)
    {
        var errors = new List<string>();
        var notes = new List<string>();
        var edits = new List<FixAction>();
        var root = request.Root;
        var name = request.Name;

        var standards = ProjectStandardsFile.Read(root).Standards;
        if (standards is null)
        {
            return Failed(Strings.Format("FixNoStandards", ProjectStandardsFile.FilePath(root)));
        }

        if (!NamePattern.IsMatch(name))
        {
            errors.Add(Strings.Get("AddInvalidName"));
        }

        var solutionFile = SolutionFull(request);
        if (!request.Solution.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || !File.Exists(solutionFile))
        {
            errors.Add(Strings.Format("AddSolutionMissing", request.Solution));
        }

        if (errors.Count > 0)
        {
            return new AddPlanResult(null, null, null, edits, TemplateStubs, errors, notes);
        }

        var folder = request.Folder ?? SolutionLayout.Infer(solutionFile).SourceFolder?.Replace("{name}", name);
        if (folder is null)
        {
            return Failed(Strings.Get("AddFolderRequired"));
        }

        folder = ElevationFiles.Normalize(folder).TrimEnd('/');
        if (!ElevationFiles.IsSafePath(folder))
        {
            return Failed(Strings.Format("AddFolderUnsafe", folder));
        }

        var projectPath = $"{folder}/{name}.csproj";
        var folderFull = FolderFull(root, projectPath);
        if (Directory.Exists(folderFull) && Directory.EnumerateFileSystemEntries(folderFull).Any())
        {
            errors.Add(Strings.Format("AddFolderNotEmpty", folder));
        }

        if (SolutionProjects.Read(solutionFile).Contains(projectPath, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(Strings.Format("SlnxAlreadyListed", projectPath));
        }

        var listed = await run(new CommandRequest
        {
            FileName = "dotnet",
            Arguments = ["new", "list", "--type", "project"],
            Timeout = TimeSpan.FromMinutes(2),
        }, ct);
        var templates = listed.Success ? TemplateList.Parse(listed.StdOut) : new TemplateListResult(false, [], listed.StdErr);
        var template = templates.Ok
            ? templates.Items.FirstOrDefault(t => string.Equals(t.ShortName, request.Template, StringComparison.OrdinalIgnoreCase))
            : null;
        if (template is null)
        {
            errors.Add(Strings.Format("AddTemplateUnknown", request.Template));
        }
        else if (!template.Languages.Contains(request.Language, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(Strings.Format("AddLanguageUnsupported", request.Template, request.Language));
        }

        var framework = request.Framework ?? standards.BaseFramework;
        foreach (var result in FrameworkGuard.Check(projectPath, [framework], standards, ProjectStandardsFile.FilePath(root)).Where(r => r.Status == GuardStatus.Refused))
        {
            errors.Add(result.Message ?? string.Empty);
        }

        foreach (var (property, value) in standards.Properties)
        {
            edits.Add(new FixAction($"{FixKind.SetProperty}:{projectPath}:{property}", projectPath, FixKind.SetProperty, property, value,
                Strings.Format("FixDescSetProperty", property, value, projectPath)));
        }

        var uses = ProjectFiles.Read(root).SelectMany(p => p.Uses).ToList();
        var isTest = name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase);
        var packages = standards.Analyzers.Concat(request.Packages)
            .Concat(isTest ? [TestSdk] : [])
            .Concat(isTest ? TestRunnerPackages(root, standards) : [])
            .Distinct(StringComparer.OrdinalIgnoreCase);

        if (request.ProjectReference is not null)
        {
            var reference = Path.GetRelativePath(folderFull, Path.Combine(root, request.ProjectReference.Replace('/', Path.DirectorySeparatorChar))).Replace('\\', '/');
            edits.Add(new FixAction($"{FixKind.AddReference}:{projectPath}:ProjectReference", projectPath, FixKind.AddReference, "ProjectReference", reference,
                Strings.Format("FixDescAddReference", projectPath, reference)));
        }
        foreach (var package in packages)
        {
            var choice = PackageVersions.Choose(uses.Where(u => string.Equals(u.Package, package, StringComparison.OrdinalIgnoreCase)).ToList());
            if (choice.Version is null)
            {
                errors.Add(Strings.Format("FixNoVersion", package));
                continue;
            }

            edits.Add(new FixAction($"{FixKind.AddPackage}:{projectPath}:{package}", projectPath, FixKind.AddPackage, package, choice.Version,
                Strings.Format("FixDescAddPackage", package, choice.Version, projectPath)));
        }

        return new AddPlanResult(null, projectPath, framework, edits, TemplateStubs, errors, notes);
    }

    private static AddPlanResult Failed(string message) => new(null, null, null, [], TemplateStubs, [message], []);

    /// <summary>Packages every existing test project references, minus the test SDK and the standard analyzers: the repo's test runner.</summary>
    private static IReadOnlyList<string> TestRunnerPackages(string root, ProjectStandards standards)
    {
        var tests = ProjectFiles.Read(root)
            .Where(p => p.Name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) || p.Name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (tests.Count == 0)
        {
            return [];
        }

        var common = tests.Select(t => t.Uses.Select(u => u.Package).ToHashSet(StringComparer.OrdinalIgnoreCase))
            .Aggregate((a, b) => { a.IntersectWith(b); return a; });
        common.Remove(TestSdk);
        foreach (var analyzer in standards.Analyzers)
        {
            common.Remove(analyzer);
        }

        return common.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Rollback(string folder, bool folderExisted, string solutionFile, byte[] solutionBytes)
    {
        File.WriteAllBytes(solutionFile, solutionBytes);
        if (!folderExisted)
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }
    }

    private static string SolutionFull(AddRequest request) => Path.Combine(request.Root, request.Solution);

    private static string FolderFull(string root, string projectPath) =>
        Path.Combine(root, projectPath[..projectPath.LastIndexOf('/')].Replace('/', Path.DirectorySeparatorChar));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string Tail(string text) => text.Length <= 2000 ? text : text[^2000..];

    private static AddConfirmResult Fail(string error) => new(false, error, [], null);
}
