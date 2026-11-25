using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class NamespaceEngineTests
{
    internal static Solution Multi(IEnumerable<(string Path, string Text)> files, string? projectPath = null, string? rootNamespace = null)
    {
        var workspace = new AdhocWorkspace();
        var info = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "P", "P", LanguageNames.CSharp, filePath: projectPath)
            .WithMetadataReferences(new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        if (rootNamespace is not null)
        {
            info = info.WithDefaultNamespace(rootNamespace);
        }

        var project = workspace.AddProject(info);
        foreach (var (path, text) in files)
        {
            project = project.AddDocument(System.IO.Path.GetFileName(path), SourceText.From(text), filePath: path).Project;
        }

        workspace.TryApplyChanges(project.Solution);
        return workspace.CurrentSolution;
    }

    internal static async Task<string> TextOfAsync(MethodEditPlan plan, string path) =>
        (await plan.Result!.Projects.Single().Documents.Single(d => d.FilePath == path).GetTextAsync()).ToString();

    private const string Lib = @"C:\proj\Lib.cs";
    private const string App = @"C:\proj\App.cs";

    private const string LibText = "namespace Old.Ns\n{\n    public class Widget { }\n}\n";

    private const string AppText =
        "using Old.Ns;\n" +
        "namespace App\n{\n    class User\n    {\n        Widget a;\n        Old.Ns.Widget b;\n        global::Old.Ns.Widget c;\n    }\n}\n";

    private static Task<MethodEditPlan> RenameAsync(IEnumerable<(string, string)> files, string oldName, string newName, bool moveFiles = false,
        string? projectPath = null, string? root = null, params CallerEdit[] callerEdits) =>
        NamespaceEngine.RenameAsync(Multi(files, projectPath, root), oldName, newName, moveFiles, callerEdits);

    [Fact]
    public async Task ARenameChangesTheDeclarationTheUsingAndEveryQualifiedName()
    {
        var plan = await RenameAsync([(Lib, LibText), (App, AppText)], "Old.Ns", "New.Core");

        Assert.False(plan.Refused);
        Assert.Contains("namespace New.Core", await TextOfAsync(plan, Lib));
        var app = await TextOfAsync(plan, App);
        Assert.Contains("using New.Core;", app);
        Assert.Contains("New.Core.Widget b;", app);
        Assert.Contains("global::New.Core.Widget c;", app);
        Assert.DoesNotContain("Old", app);
        Assert.Equal(1, plan.DeclarationCount);
    }

    [Fact]
    public async Task NestedNamespacesFollowTheRename()
    {
        const string child = "namespace Old.Ns.Child\n{\n    public class Leaf { }\n}\n";
        const string use = "namespace Z\n{\n    class U { Old.Ns.Child.Leaf l; }\n}\n";

        var plan = await RenameAsync([(Lib, LibText), (@"C:\proj\Child.cs", child), (App, use)], "Old.Ns", "New.Core");

        Assert.False(plan.Refused);
        Assert.Contains("namespace New.Core.Child", await TextOfAsync(plan, @"C:\proj\Child.cs"));
        Assert.Contains("New.Core.Child.Leaf l;", await TextOfAsync(plan, App));
        Assert.Equal(2, plan.DeclarationCount);
    }

    [Fact]
    public async Task AFileScopedNamespaceIsRenamed()
    {
        var plan = await RenameAsync([(Lib, "namespace Old.Ns;\n\npublic class Widget { }\n")], "Old.Ns", "New.Core");

        Assert.False(plan.Refused);
        Assert.Contains("namespace New.Core;", await TextOfAsync(plan, Lib));
    }

    [Fact]
    public async Task ATypeThatWouldCollideIsRefused()
    {
        const string other = "namespace New.Core\n{\n    public class Widget { }\n}\n";

        var plan = await RenameAsync([(Lib, LibText), (@"C:\proj\Other.cs", other)], "Old.Ns", "New.Core");

        Assert.True(plan.Refused);
        Assert.Contains(plan.Reasons, r => r.Contains("Widget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARelativeReferenceIsRefusedUnlessACallerEditCoversIt()
    {
        const string relative = "namespace Old\n{\n    class X { Ns.Widget w; }\n}\n";

        var refused = await RenameAsync([(Lib, LibText), (App, relative)], "Old.Ns", "New.Core");
        Assert.True(refused.Refused);
        Assert.Single(refused.Uncovered);

        var (line, column) = MethodEditEngineTests.Locate(relative, "Ns.Widget");
        var covered = await RenameAsync([(Lib, LibText), (App, relative)], "Old.Ns", "New.Core", callerEdits: new CallerEdit(App, line, column, "New.Core"));
        Assert.False(covered.Refused);
        Assert.Contains("New.Core.Widget w;", await TextOfAsync(covered, App));
    }

    [Fact]
    public async Task ABlockNestedInsideAnotherNamespaceBlockIsRefused()
    {
        var plan = await RenameAsync([(Lib, "namespace Old\n{\n    namespace Ns\n    {\n        public class Widget { }\n    }\n}\n")], "Old.Ns", "New.Core");

        Assert.True(plan.Refused);
    }

    [Fact]
    public async Task AStringThatMentionsTheOldNameIsReportedNotChanged()
    {
        const string text = "namespace K\n{\n    class R { string t = \"Old.Ns.Widget, P\"; }\n}\n";

        var plan = await RenameAsync([(Lib, LibText), (App, text)], "Old.Ns", "New.Core");

        Assert.False(plan.Refused);
        Assert.Single(plan.StringMentions);
        Assert.Contains("\"Old.Ns.Widget, P\"", await TextOfAsync(plan, App));
    }

    [Fact]
    public async Task AnUnknownNamespaceAndABadNameAreRefused()
    {
        Assert.True((await RenameAsync([(Lib, LibText)], "No.Such", "New.Core")).Refused);
        Assert.True((await RenameAsync([(Lib, LibText)], "Old.Ns", "New..Core")).Refused);
        Assert.True((await RenameAsync([(Lib, LibText)], "Old.Ns", "New.class")).Refused);
        Assert.True((await RenameAsync([(Lib, LibText)], "Old.Ns", "Old.Ns")).Refused);
    }

    [Fact]
    public async Task AProjectThatFollowsTheFolderConventionGetsMovesOrAWarning()
    {
        var files = new[] { (@"C:\proj\Sub\A.cs", "namespace P.Sub\n{\n    public class A { }\n}\n") };

        var move = await RenameAsync(files, "P.Sub", "P.Other", moveFiles: true, projectPath: @"C:\proj\P.csproj", root: "P");
        Assert.False(move.Refused);
        var m = Assert.Single(move.FileMoves);
        Assert.Equal(@"C:\proj\Sub\A.cs", m.From);
        Assert.Equal(@"C:\proj\Other\A.cs", m.To);

        var warn = await RenameAsync(files, "P.Sub", "P.Other", moveFiles: false, projectPath: @"C:\proj\P.csproj", root: "P");
        Assert.Empty(warn.FileMoves);
        Assert.Contains(warn.Warnings, w => w.Contains("folder", StringComparison.Ordinal));
    }
}
