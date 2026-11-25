using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class MethodEditEngineTests
{
    internal const string File = @"C:\scratch\a.cs";

    private const string Source =
        "class C {\n" +
        "    public int Add(int a, int b) { return a + b; }\n" +
        "    public int Use() { return Add(1, 2); }\n" +
        "}\n";

    internal static Solution Create(string code)
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp)
            .WithMetadataReferences(new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        var document = project.AddDocument("a.cs", SourceText.From(code), filePath: File);
        workspace.TryApplyChanges(document.Project.Solution);
        return workspace.CurrentSolution;
    }

    // 1-based line and column of the first occurrence of a marker in the source.
    internal static (int Line, int Column) Locate(string code, string marker)
    {
        var index = code.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, marker);
        var line = code.Take(index).Count(c => c == '\n') + 1;
        var column = index - (code.LastIndexOf('\n', index) + 1) + 1;
        return (line, column);
    }

    private static Task<MethodEditPlan> PlanAsync(string code, string newText, params CallerEdit[] callerEdits)
    {
        var (line, column) = Locate(code, "Add(int");
        return MethodEditEngine.PlanAsync(Create(code), File, line, column, newText, callerEdits);
    }

    private const string Documented =
        "class C {\n" +
        "    /// <summary>Adds two numbers.</summary>\n" +
        "    /// <param name=\"a\">The first number.</param>\n" +
        "    /// <param name=\"b\">The second number, see <paramref name=\"a\"/>.</param>\n" +
        "    /// <returns>The sum.</returns>\n" +
        "    public int Add(int a, int b) { return a + b; }\n" +
        "}\n";

    private static Task<MethodEditPlan> PlanDocsAsync(string code, string newText, DocUpdates docs)
    {
        var (line, column) = Locate(code, "Add(int");
        return MethodEditEngine.PlanAsync(Create(code), File, line, column, newText, Array.Empty<CallerEdit>(), docs);
    }

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task ANewParameterOnADocumentedMethodNeedsDocText()
    {
        var plan = await PlanDocsAsync(Documented, "public int Add(int a, int b, int c) { return a + b + c; }", new DocUpdates());

        Assert.True(plan.Refused);
        Assert.Contains(plan.Reasons, r => r.Contains("param c", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANewParameterWithTextIsDocumentedInTheSameStyle()
    {
        var docs = new DocUpdates();
        docs.Items.Add(new DocItem("param", "c", "The third number."));

        var plan = await PlanDocsAsync(Documented, "public int Add(int a, int b, int c) { return a + b + c; }", docs);

        Assert.False(plan.Refused);
        Assert.True(plan.DocsUpdated);
        var text = await TextOfAsync(plan);
        Assert.Contains("    /// <param name=\"b\">The second number, see <paramref name=\"a\"/>.</param>\n    /// <param name=\"c\">The third number.</param>\n    /// <returns>The sum.</returns>\n    public int Add(", text);
        Assert.Contains("    /// <summary>Adds two numbers.</summary>\n", text);
    }

    [Fact]
    public async Task ARemovedParameterLosesItsTag()
    {
        var plan = await PlanDocsAsync(Documented, "public int Add(int a) { return a; }", new DocUpdates());

        Assert.False(plan.Refused);
        var text = await TextOfAsync(plan);
        Assert.DoesNotContain("name=\"b\"", text);
        Assert.Contains("<param name=\"a\">The first number.</param>", text);
    }

    [Fact]
    public async Task ARenamedParameterKeepsItsTextAndParamrefs()
    {
        var plan = await PlanDocsAsync(Documented, "public int Add(int total, int b) { return total + b; }", new DocUpdates());

        Assert.False(plan.Refused);
        var text = await TextOfAsync(plan);
        Assert.Contains("<param name=\"total\">The first number.</param>", text);
        Assert.Contains("<paramref name=\"total\"/>", text);
        Assert.DoesNotContain("name=\"a\"", text);
    }

    [Fact]
    public async Task ANewSummaryReplacesTheSummaryOnABodyOnlyEdit()
    {
        var docs = new DocUpdates { NewSummary = "Sums two numbers, saturating at the maximum." };

        var plan = await PlanDocsAsync(Documented, "public int Add(int a, int b) { return a + b; }", docs);

        Assert.False(plan.Refused);
        Assert.False(plan.SignatureChanged);
        var text = await TextOfAsync(plan);
        Assert.Contains("    /// <summary>Sums two numbers, saturating at the maximum.</summary>\n", text);
        Assert.Contains("<returns>The sum.</returns>", text);
    }

    [Fact]
    public async Task AMultiLineSummaryKeepsItsLayoutWhenAParameterIsAdded()
    {
        const string code =
            "class C {\n" +
            "    /// <summary>\n" +
            "    /// Adds two numbers.\n" +
            "    /// </summary>\n" +
            "    /// <param name=\"a\">First.</param>\n" +
            "    /// <param name=\"b\">Second.</param>\n" +
            "    public void Add(int a, int b) { }\n" +
            "}\n";
        var docs = new DocUpdates();
        docs.Items.Add(new DocItem("param", "c", "Third."));

        var plan = await PlanDocsAsync(code.Replace("public void", "public int"), "public void Add(int a, int b, int c) { }", docs);

        Assert.False(plan.Refused);
        var text = await TextOfAsync(plan);
        Assert.Contains("    /// <summary>\n    /// Adds two numbers.\n    /// </summary>\n", text);
        Assert.Contains("    /// <param name=\"c\">Third.</param>\n    public void Add(", text);
    }

    [Fact]
    public async Task AMethodWithoutADocCommentGetsNoDocs()
    {
        var plan = await PlanAsync(Source, "public int Add(int a, int b) { return a - b; }");

        Assert.False(plan.Refused);
        Assert.False(plan.DocsUpdated);
        Assert.DoesNotContain("///", await TextOfAsync(plan));
    }

    [Fact]
    public async Task NewTextThatCarriesItsOwnDocCommentIsRefused()
    {
        var plan = await PlanAsync(Source, "/// <summary>x</summary>\npublic int Add(int a, int b) { return a; }");

        Assert.True(plan.Refused);
    }

    [Fact]
    public async Task BodyOnlyEditIsAllowedAndKeepsTheSignature()
    {
        var plan = await PlanAsync(Source, "public int Add(int a, int b) { return b + a; }");

        Assert.False(plan.Refused);
        Assert.False(plan.SignatureChanged);
        Assert.NotNull(plan.Result);
    }

    [Fact]
    public async Task SignatureChangeWithAnUncoveredCallerIsRefused()
    {
        var plan = await PlanAsync(Source, "public int Add(int a, int b, int c) { return a + b + c; }");

        Assert.True(plan.Refused);
        Assert.True(plan.SignatureChanged);
        Assert.Single(plan.Uncovered);
        Assert.Null(plan.Result);
    }

    [Fact]
    public async Task SignatureChangeWithACallerEditIsAllowedAndEditsTheCaller()
    {
        var (line, column) = Locate(Source, "Add(1");
        var plan = await PlanAsync(
            Source,
            "public int Add(int a, int b, int c) { return a + b + c; }",
            new CallerEdit(File, line, column, "Add(1, 2, 3)"));

        Assert.False(plan.Refused);
        Assert.True(plan.SignatureChanged);
        var text = (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();
        Assert.Contains("return Add(1, 2, 3);", text);
        Assert.Contains("int c", text);
    }

    [Fact]
    public async Task AnEditThatAddsACompileErrorIsRefused()
    {
        var plan = await PlanAsync(Source, "public int Add(int a, int b) { return missing; }");

        Assert.True(plan.Refused);
        Assert.NotEmpty(plan.NewErrors);
        Assert.Null(plan.Result);
    }

    [Fact]
    public async Task ACallerEditThatIsNotAReferenceIsRefused()
    {
        var plan = await PlanAsync(Source, "public int Add(int a, int b) { return a - b; }", new CallerEdit(File, 1, 1, "x"));

        Assert.True(plan.Refused);
        Assert.Null(plan.Result);
    }

    [Fact]
    public async Task NewTextThatIsNotOneMethodIsRefused()
    {
        var plan = await PlanAsync(Source, "int x = 1;");

        Assert.True(plan.Refused);
    }

    [Fact]
    public async Task SignatureChangeOnAnOverriddenMethodIsRefused()
    {
        const string code =
            "class A {\n" +
            "    public virtual int Add(int a, int b) { return a + b; }\n" +
            "}\n" +
            "class B : A {\n" +
            "    public override int Add(int a, int b) { return a; }\n" +
            "}\n";

        var plan = await PlanAsync(code, "public virtual int Add(int a) { return a; }");

        Assert.True(plan.Refused);
        Assert.Contains(plan.Reasons, r => r.Contains("B.Add", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReferenceInsideTheMethodItselfNeedsNoCallerEdit()
    {
        const string code =
            "class C {\n" +
            "    public int Add(int a, int b) { return a == 0 ? b : Add(a - 1, b + 1); }\n" +
            "}\n";

        var plan = await PlanAsync(code, "public int Add(int a, int b, int c) { return a == 0 ? b : Add(a - 1, b + 1, c); }");

        Assert.False(plan.Refused);
        Assert.True(plan.SignatureChanged);
    }
}
