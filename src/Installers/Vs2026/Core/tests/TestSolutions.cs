using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

/// <summary>
/// Builds <see cref="Solution"/>s for the engine tests on an <see cref="AdhocWorkspace"/>. One project per call by default; a project can
/// target several frameworks, and a file can be linked into a second project under the same path (the multi-target and linked cases).
/// </summary>
internal static class TestSolutions
{
    // Plain classes: the net48 test compilation has no init-only support for records.
    public sealed class SourceFile
    {
        public SourceFile(string path, string text)
        {
            Path = path;
            Text = text;
        }

        public string Path { get; }

        public string Text { get; }
    }

    public sealed class ProjectSpec
    {
        public ProjectSpec(string name, IReadOnlyList<SourceFile> files, string? filePath = null, string? rootNamespace = null)
        {
            Name = name;
            Files = files;
            FilePath = filePath;
            RootNamespace = rootNamespace;
        }

        public string Name { get; }

        public IReadOnlyList<SourceFile> Files { get; }

        public string? FilePath { get; }

        public string? RootNamespace { get; }
    }

    /// <summary>The metadata references every test project gets: the core library only, so tests stay deterministic.</summary>
    private static readonly MetadataReference[] Core = [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];

    /// <summary>One project named "P" holding the files.</summary>
    public static Solution Single(IEnumerable<(string Path, string Text)> files, string? projectPath = null, string? rootNamespace = null) =>
        Build([new ProjectSpec("P", files.Select(f => new SourceFile(f.Path, f.Text)).ToList(), projectPath, rootNamespace)]);

    /// <summary>
    /// Several projects in one solution. A file that appears in two projects is linked: it has one path and two documents.
    /// </summary>
    public static Solution Build(IEnumerable<ProjectSpec> projects)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        foreach (var spec in projects)
        {
            var info = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), spec.Name, spec.Name, LanguageNames.CSharp, filePath: spec.FilePath)
                .WithMetadataReferences(Core);
            if (spec.RootNamespace is not null)
            {
                info = info.WithDefaultNamespace(spec.RootNamespace);
            }

            solution = solution.AddProject(info);
            var projectId = solution.ProjectIds.Last();
            foreach (var file in spec.Files)
            {
                solution = solution.AddDocument(DocumentId.CreateNewId(projectId), System.IO.Path.GetFileName(file.Path), SourceText.From(file.Text), filePath: file.Path);
            }
        }

        return solution;
    }

    /// <summary>The text of the document at a path, in the first project that has it.</summary>
    public static async System.Threading.Tasks.Task<string> TextAsync(Solution solution, string path)
    {
        var document = solution.Projects.SelectMany(p => p.Documents).First(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
        return (await document.GetTextAsync()).ToString();
    }

    /// <summary>The documents a path resolves to, one per project it is in.</summary>
    public static IReadOnlyList<DocumentId> DocumentsAt(Solution solution, string path) =>
        solution.GetDocumentIdsWithFilePath(path).ToList();
}
