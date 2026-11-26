using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class ConvertEngineTests
{
    private const string File = @"C:\scratch\convert.cs";

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task APrivateFieldBecomesAProperty()
    {
        const string code = "class C\n{\n    private int _count;\n    int Get() => _count;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ConvertEngine.PlanAsync(solution, File, 3, "field-to-property");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("private int Count { get; set; }", text);
        Assert.Contains("int Get() => Count;", text);
        Assert.DoesNotContain("_count", text);
    }

    [Fact]
    public async Task ANullGuardIsAddedForAReferenceParameter()
    {
        const string code = "class C\n{\n    void Take(string name)\n    {\n        System.Console.Write(name);\n    }\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ConvertEngine.PlanAsync(solution, File, 3, "null-guards");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("if (name == null) throw new System.ArgumentNullException(nameof(name));", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AnExpressionBodyBecomesABlock()
    {
        const string code = "class C\n{\n    int Get() => 4;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ConvertEngine.PlanAsync(solution, File, 3, "expression-to-block");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("{ return 4; }", await TextOfAsync(plan));
    }

    [Fact]
    public async Task ASingleReturnBlockBecomesAnExpressionBody()
    {
        const string code = "class C\n{\n    int Get()\n    {\n        return 4;\n    }\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ConvertEngine.PlanAsync(solution, File, 3, "block-to-expression");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("=> 4;", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AVarBecomesItsExplicitType()
    {
        const string code = "class C\n{\n    void M()\n    {\n        var n = 4;\n    }\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ConvertEngine.PlanAsync(solution, File, 5, "var-to-explicit");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("int n = 4;", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AStringConcatenationBecomesAnInterpolation()
    {
        const string code = "class C\n{\n    string M(string who)\n    {\n        return \"hi \" + who + \"!\";\n    }\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ConvertEngine.PlanAsync(solution, File, 5, "string-to-interpolation");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("$\"hi {who}!\"", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AClassThatOverridesEqualsIsNotMadeARecord()
    {
        const string code = "class C\n{\n    public override bool Equals(object o) => false;\n    public override int GetHashCode() => 0;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ConvertEngine.PlanAsync(solution, File, 1, "class-to-record");

        Assert.True(plan.Refused);
    }

    [Fact]
    public async Task AGetterOnlyPropertyBecomesAMethod()
    {
        const string code = "class C\n{\n    public int Size => 3;\n    int Use() => Size;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ConvertEngine.PlanAsync(solution, File, 3, "property-to-method");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("GetSize()", text);
        Assert.Contains("Use() => GetSize();", text);
    }

    [Fact]
    public async Task AMethodWithNoCallersBecomesAsync()
    {
        const string code = "class C\n{\n    public int Get() => 3;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ConvertEngine.PlanAsync(solution, File, 3, "make-async");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("async", text);
        Assert.Contains("Task<int> Get()", text);
    }

    [Fact]
    public async Task AMethodWithCallersIsRefusedForAsync()
    {
        const string code = "class C\n{\n    public int Get() => 3;\n    int Use() => Get();\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ConvertEngine.PlanAsync(solution, File, 3, "make-async");

        Assert.True(plan.Refused);
    }

    [Fact]
    public async Task AMethodToPropertyDropsTheCallParentheses()
    {
        const string code = "class C\n{\n    public int Size() => 3;\n    int Use() => Size();\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ConvertEngine.PlanAsync(solution, File, 3, "method-to-property");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("public int Size => 3;", text);
        Assert.Contains("int Use() => Size;", text);
    }

    [Fact]
    public async Task APlainClassBecomesAStruct()
    {
        const string code = "class P\n{\n    public int X { get; set; }\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ConvertEngine.PlanAsync(solution, File, 1, "class-to-struct");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("struct P", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AsyncThreadsTheCallerAndAwaitsTheCall()
    {
        const string code = "class C\n{\n    public int Get() => 3;\n    int Use() => Get();\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await AsyncEngine.PlanAsync(solution, File, 3, 1, false);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("Task<int> Get()", text);
        Assert.Contains("await Get()", text);
        Assert.Contains("Task<int> Use()", text);
    }

    [Fact]
    public async Task AsyncWithSuffixRenamesTheMethodAndItsCalls()
    {
        const string code = "class C\n{\n    public int Get() => 3;\n    int Use() => Get();\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await AsyncEngine.PlanAsync(solution, File, 3, 1, true);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("Task<int> GetAsync()", text);
        Assert.Contains("await GetAsync()", text);
    }

    [Fact]
    public async Task AGetOnlyAutoPropertyWithAConstantBecomesAnExpressionBody()
    {
        const string code = "class C\n{\n    public int X { get; } = 3;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ConvertEngine.PlanAsync(solution, File, 3, "auto-to-expression");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("public int X => 3;", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AnExpressionBodyPropertyBecomesAGetter()
    {
        const string code = "class C\n{\n    public int X => 3;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ConvertEngine.PlanAsync(solution, File, 3, "expression-to-auto");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("{ get { return 3; } }", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AnUnknownKindIsRefused()
    {
        const string code = "class C\n{\n    int _x;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ConvertEngine.PlanAsync(solution, File, 3, "rotate");

        Assert.True(plan.Refused);
    }
}
