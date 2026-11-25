using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class TddEngineTests
{
    private const string ProdFile = @"C:\scratch\Calc.cs";
    private const string TestFile = @"C:\scratch\CalcTests.cs";

    private const string Prod =
        "public class Calc\n" +
        "{\n" +
        "    public int Add(int a, int b) { return a + b; }\n" +
        "}\n";

    private const string Tests =
        "public class FactAttribute : System.Attribute { }\n" +
        "public class CalcTests\n" +
        "{\n" +
        "    [Fact]\n" +
        "    public void AddWorks() { var c = new Calc(); if (c.Add(1, 2) != 3) throw new System.Exception(\"x\"); }\n" +
        "}\n";

    private static Solution TwoFiles(string prod, string tests)
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp)
            .WithMetadataReferences(new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        project = project.AddDocument("Calc.cs", SourceText.From(prod), filePath: ProdFile).Project;
        project = project.AddDocument("CalcTests.cs", SourceText.From(tests), filePath: TestFile).Project;
        workspace.TryApplyChanges(project.Solution);
        return workspace.CurrentSolution;
    }

    private static async Task<string> TextAsync(MethodEditPlan plan, string path)
    {
        var document = plan.Result!.Projects.Single().Documents.Single(d => d.FilePath == path);
        return (await document.GetTextAsync()).ToString();
    }

    [Fact]
    public async Task AnEditNamesTheTestsThatCallTheMethodAndWhereTheMethodSits()
    {
        var (line, column) = MethodEditEngineTests.Locate(Prod, "Add(int");

        var plan = await MethodEditEngine.PlanAsync(
            TwoFiles(Prod, Tests), ProdFile, line, column, "public int Add(int a, int b) { return b + a; }", Array.Empty<CallerEdit>());

        Assert.False(plan.Refused);
        var test = Assert.Single(plan.Tests);
        Assert.Equal("CalcTests.AddWorks", test.Name);
        Assert.Equal(TestFile, test.File);
        var reply = System.Text.Json.JsonSerializer.Serialize(plan.ToReply());
        Assert.Contains("\"start_line\":3", reply);
        Assert.Contains("\"end_line\":3", reply);
    }

    [Fact]
    public async Task AMethodNobodyTestsHasNoDirectTests()
    {
        const string prod = "public class Calc\n{\n    public int Other() { return 1; }\n}\n";
        var (line, column) = MethodEditEngineTests.Locate(prod, "Other()");

        var plan = await MethodEditEngine.PlanAsync(
            TwoFiles(prod, Tests.Replace("c.Add(1, 2) != 3", "false")), ProdFile, line, column, "public int Other() { return 2; }", Array.Empty<CallerEdit>());

        Assert.False(plan.Refused);
        Assert.Empty(plan.Tests);
    }

    [Fact]
    public async Task StartingAddsAThrowingStubAndATestThatCallsIt()
    {
        var (methodLine, _) = MethodEditEngineTests.Locate(Prod, "Add(int");
        var (testLine, _) = MethodEditEngineTests.Locate(Tests, "AddWorks");
        const string test = "[Fact]\npublic void TwiceDoubles() { if (new Calc().Twice(2) != 4) throw new System.Exception(\"x\"); }";

        var plan = await TddEngine.StartAsync(TwoFiles(Prod, Tests), TestFile, testLine, test, ProdFile, methodLine, "public int Twice(int x);", new DocUpdates());

        Assert.False(plan.Refused);
        Assert.Contains("throw new System.NotImplementedException();", await TextAsync(plan, ProdFile));
        Assert.Contains("TwiceDoubles", await TextAsync(plan, TestFile));
        var target = Assert.Single(plan.Tests);
        Assert.Equal("CalcTests.TwiceDoubles", target.Name);
    }

    [Fact]
    public async Task StartingInOneFileIsRefused()
    {
        var plan = await TddEngine.StartAsync(TwoFiles(Prod, Tests), ProdFile, 1, "[Fact]\npublic void T() { }", ProdFile, 1, "public int Twice(int x);", new DocUpdates());

        Assert.True(plan.Refused);
    }

    [Fact]
    public async Task StartingWithATestThatHasNoTestAttributeIsRefused()
    {
        var (methodLine, _) = MethodEditEngineTests.Locate(Prod, "Add(int");
        var (testLine, _) = MethodEditEngineTests.Locate(Tests, "AddWorks");

        var plan = await TddEngine.StartAsync(
            TwoFiles(Prod, Tests), TestFile, testLine, "public void NotATest() { }", ProdFile, methodLine, "public int Twice(int x);", new DocUpdates());

        Assert.True(plan.Refused);
        Assert.Contains(plan.Reasons, r => r.Contains("test attribute", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdatingATestReplacesItsBodyAndTargetsThatTest()
    {
        var (testLine, _) = MethodEditEngineTests.Locate(Tests, "AddWorks");
        var (targetLine, _) = MethodEditEngineTests.Locate(Prod, "Add(int");

        var plan = await TddEngine.UpdateTestAsync(
            TwoFiles(Prod, Tests), TestFile, testLine, "var c = new Calc(); if (c.Add(2, 2) != 4) throw new System.Exception(\"y\");", ProdFile, targetLine);

        Assert.False(plan.Refused);
        var text = await TextAsync(plan, TestFile);
        Assert.Contains("c.Add(2, 2) != 4", text);
        Assert.DoesNotContain("c.Add(1, 2)", text);
        Assert.Contains("[Fact]", text);
        Assert.Equal("CalcTests.AddWorks", Assert.Single(plan.Tests).Name);
        Assert.Contains("\"start_line\":3", System.Text.Json.JsonSerializer.Serialize(plan.ToReply()));
    }

    [Fact]
    public async Task UpdatingAMethodThatIsNotATestIsRefused()
    {
        var (line, _) = MethodEditEngineTests.Locate(Prod, "Add(int");

        var plan = await TddEngine.UpdateTestAsync(TwoFiles(Prod, Tests), ProdFile, line, "return 1;", null, null);

        Assert.True(plan.Refused);
    }
}
