using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class MethodAddEngineTests
{
    private const string File = MethodEditEngineTests.File;

    private const string Documented =
        "class C {\n" +
        "    /// <summary>Adds two numbers.</summary>\n" +
        "    /// <param name=\"a\">The first number.</param>\n" +
        "    /// <param name=\"b\">The second number.</param>\n" +
        "    /// <returns>The sum.</returns>\n" +
        "    public int Add(int a, int b) { return a + b; }\n" +
        "\n" +
        "    public int Last() { return 0; }\n" +
        "}\n";

    private static Task<MethodEditPlan> AddAsync(string code, string marker, string newText, DocUpdates? docs = null, int? afterLine = null, bool overload = false)
    {
        var (line, _) = MethodEditEngineTests.Locate(code, marker);
        return MethodAddEngine.PlanAsync(MethodEditEngineTests.Create(code), File, line, newText, afterLine, overload, docs ?? new DocUpdates());
    }

    private static DocUpdates TwiceDocs()
    {
        var docs = new DocUpdates { NewSummary = "Doubles a number." };
        docs.Items.Add(new DocItem("param", "x", "The number."));
        docs.Items.Add(new DocItem("returns", null, "Twice the number."));
        return docs;
    }

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task AMethodIsAddedAtTheEndOfTheTypeWithDocsInTheNeighboursStyle()
    {
        var plan = await AddAsync(Documented, "Add(int", "public int Twice(int x) { return x * 2; }", TwiceDocs());

        Assert.False(plan.Refused);
        Assert.True(plan.DocsUpdated);
        var text = await TextOfAsync(plan);
        Assert.Contains("    public int Last() { return 0; }\n\n    /// <summary>Doubles a number.</summary>\n    /// <param name=\"x\">The number.</param>\n    /// <returns>Twice the number.</returns>\n    public int Twice(int x) { return x * 2; }\n}", text);
    }

    [Fact]
    public async Task AMethodAddedAfterALineGoesBetweenTheMembers()
    {
        var (line, _) = MethodEditEngineTests.Locate(Documented, "public int Add(int");
        var plan = await AddAsync(Documented, "Last()", "public int Twice(int x) { return x * 2; }", TwiceDocs(), afterLine: line);

        Assert.False(plan.Refused);
        var text = await TextOfAsync(plan);
        Assert.True(text.IndexOf("Twice(", StringComparison.Ordinal) > text.IndexOf("int Add(", StringComparison.Ordinal));
        Assert.True(text.IndexOf("Twice(", StringComparison.Ordinal) < text.IndexOf("int Last(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingDocTextIsAWarningNotAnInventedComment()
    {
        var plan = await AddAsync(Documented, "Add(int", "public int Twice(int x) { return x * 2; }");

        Assert.False(plan.Refused);
        Assert.False(plan.DocsUpdated);
        Assert.Contains(plan.Warnings, w => w.Contains("summary", StringComparison.Ordinal));
        var text = await TextOfAsync(plan);
        Assert.Contains("public int Twice(int x)", text);
        Assert.Single(Regex.Matches(text, "<summary>").Cast<Match>());
    }

    [Fact]
    public async Task AMethodWithTheSameSignatureIsRefused()
    {
        var plan = await AddAsync(Documented, "Add(int", "public int Add(int a, int b) { return 0; }", TwiceDocs());

        Assert.True(plan.Refused);
        Assert.NotEmpty(plan.NewErrors);
    }

    [Fact]
    public async Task AnOverloadFollowsTheOriginalWithDocsBuiltFromIt()
    {
        var docs = new DocUpdates();
        docs.Items.Add(new DocItem("param", "c", "The third number."));

        var plan = await AddAsync(Documented, "Add(int", "public int Add(int a, int b, int c) { return a + b + c; }", docs, overload: true);

        Assert.False(plan.Refused);
        var text = await TextOfAsync(plan);
        Assert.Contains("    public int Add(int a, int b) { return a + b; }\n\n    /// <summary>Adds two numbers.</summary>\n    /// <param name=\"a\">The first number.</param>\n    /// <param name=\"b\">The second number.</param>\n    /// <param name=\"c\">The third number.</param>\n    /// <returns>The sum.</returns>\n    public int Add(int a, int b, int c)", text);
    }

    [Fact]
    public async Task AnOverloadThatTakesOverAnExistingCallIsRefused()
    {
        const string code =
            "class C {\n" +
            "    public void Log(object o) { }\n" +
            "    public void Use() { Log(5); }\n" +
            "}\n";

        var plan = await AddAsync(code, "Log(object", "public void Log(int i) { }", overload: true);

        Assert.True(plan.Refused);
        Assert.Single(plan.Rebinds);
    }

    [Fact]
    public async Task AnOverloadThatLeavesCallsAloneIsAllowed()
    {
        const string code =
            "class C {\n" +
            "    public void Log(int i) { }\n" +
            "    public void Use() { Log(5); }\n" +
            "}\n";

        var plan = await AddAsync(code, "Log(int", "public void Log(int i, string tag) { }", overload: true);

        Assert.False(plan.Refused);
        Assert.Empty(plan.Rebinds);
    }

    [Fact]
    public async Task AnOverloadWithAnotherNameIsRefused()
    {
        var plan = await AddAsync(Documented, "Add(int", "public int Plus(int a) { return a; }", overload: true);

        Assert.True(plan.Refused);
    }

    [Fact]
    public async Task EditingFormatsTheFileAndReportsNewWarnings()
    {
        var (line, column) = MethodEditEngineTests.Locate(Documented, "Add(int");
        var plan = await MethodEditEngine.PlanAsync(
            MethodEditEngineTests.Create(Documented), File, line, column,
            "public   int Add(int a,int b){ int unused = 0; return a+b; }", Array.Empty<CallerEdit>());

        Assert.False(plan.Refused);
        Assert.True(plan.Formatted);
        Assert.Contains(plan.NewWarnings, w => w.ToString()!.Contains("CS0219", StringComparison.Ordinal));
        Assert.Contains("public int Add(int a, int b)", await TextOfAsync(plan));
    }

    [Fact]
    public async Task FormattingKeepsTheFilesLineEndings()
    {
        var crlf = Documented.Replace("\n", "\r\n");
        var (line, column) = MethodEditEngineTests.Locate(crlf, "Add(int");
        var plan = await MethodEditEngine.PlanAsync(
            MethodEditEngineTests.Create(crlf), File, line, column,
            "public int Add(int a,int b){ return a+b; }", Array.Empty<CallerEdit>());

        Assert.False(plan.Refused);
        Assert.DoesNotMatch("(?<!\r)\n", await TextOfAsync(plan));
    }

    [Fact]
    public async Task FormattingKeepsAFilesLfLineEndings()
    {
        var (line, column) = MethodEditEngineTests.Locate(Documented, "Add(int");
        var plan = await MethodEditEngine.PlanAsync(
            MethodEditEngineTests.Create(Documented), File, line, column,
            "public int Add(int a,int b){ return a+b; }", Array.Empty<CallerEdit>());

        Assert.False(plan.Refused);
        Assert.DoesNotContain("\r", await TextOfAsync(plan), StringComparison.Ordinal);
    }
}
