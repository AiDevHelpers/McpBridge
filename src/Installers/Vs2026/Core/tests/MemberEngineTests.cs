using Microsoft.CodeAnalysis;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class MemberEngineTests
{
    private const string File = MethodEditEngineTests.File;

    // The test compilation targets .NET Framework, which has none of the types required and init members need.
    private const string Polyfills =
        "namespace System.Runtime.CompilerServices { public class IsExternalInit { } public class RequiredMemberAttribute : System.Attribute { } public class CompilerFeatureRequiredAttribute : System.Attribute { public CompilerFeatureRequiredAttribute(string n) { } } }\n";

    private static MemberSpec Spec(string name, string type, string accessibility = "public", string backing = "auto", Action<MemberSpec>? tweak = null)
    {
        var spec = new MemberSpec { Name = name, Type = type, Accessibility = accessibility, Backing = backing };
        tweak?.Invoke(spec);
        return spec;
    }

    private static async Task<string> TextAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    private static Task<MethodEditPlan> AddAsync(string code, string marker, params MemberSpec[] specs)
    {
        var (line, _) = MethodEditEngineTests.Locate(code, marker);
        return MemberAddEngine.PlanAsync(MethodEditEngineTests.Create(code), File, line, specs, finalize: true);
    }

    private static Task<MethodEditPlan> EditAsync(string code, string marker, MemberEdit edit, params CallerEdit[] callerEdits)
    {
        var (line, _) = MethodEditEngineTests.Locate(code, marker);
        return MemberEditEngine.PlanAsync(MethodEditEngineTests.Create(code), File, line, edit, callerEdits);
    }

    // ---- adding ----

    [Fact]
    public async Task ARequiredAutoPropertyIsAddedWithItsPrivacy()
    {
        var plan = await AddAsync(Polyfills + "class C\n{\n}\n", "class C\n{", Spec("Title", "string", tweak: s => s.Required = true));

        Assert.False(plan.Refused);
        Assert.Contains("public required string Title { get; set; }", await TextAsync(plan));
    }

    [Fact]
    public async Task AFieldBackedPropertyGetsAPrivateFieldAndAccessorsOverIt()
    {
        var plan = await AddAsync("class C\n{\n}\n", "class C", Spec("Count", "int", backing: "field", tweak: s => s.Initializer = "3"));

        Assert.False(plan.Refused);
        var text = await TextAsync(plan);
        Assert.Contains("private int _count = 3;", text);
        Assert.Contains("get => _count;", text);
        Assert.Contains("set => _count = value;", text);
    }

    [Fact]
    public async Task AReadonlyFieldAndAnInitOnlyPropertyAreRendered()
    {
        var plan = await AddAsync(
            Polyfills + "class C\n{\n}\n", "class C\n{",
            Spec("_id", "int", "private", "readonly-field", s => s.Initializer = "5"),
            Spec("Name", "string", tweak: s => s.Setter = "init"));

        Assert.False(plan.Refused);
        var text = await TextAsync(plan);
        Assert.Contains("private readonly int _id = 5;", text);
        Assert.Contains("public string Name { get; init; }", text);
    }

    [Fact]
    public async Task APropertyWithAPrivateSetterKeepsItsPrivacy()
    {
        var plan = await AddAsync("class C\n{\n}\n", "class C", Spec("Total", "int", "internal", tweak: s => s.Setter = "private set"));

        Assert.Contains("internal int Total { get; private set; }", await TextAsync(plan));
    }

    [Fact]
    public async Task AMemberWithoutAccessibilityIsRefused()
    {
        var plan = await AddAsync("class C\n{\n}\n", "class C", Spec("Title", "string", accessibility: ""));

        Assert.True(plan.Refused);
        Assert.Contains(plan.Reasons, r => r.Contains("Accessibility", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMemberThatAlreadyExistsIsRefused()
    {
        var plan = await AddAsync("class C\n{\n    public int Title { get; set; }\n}\n", "class C", Spec("Title", "string"));

        Assert.True(plan.Refused);
    }

    [Fact]
    public async Task ANewMemberFollowsTheLastPropertyAndTakesTheNeighboursDocStyle()
    {
        const string code =
            "class C\n" +
            "{\n" +
            "    /// <summary>The name.</summary>\n" +
            "    public string Name { get; set; }\n" +
            "\n" +
            "    public void Run() { }\n" +
            "}\n";

        var plan = await AddAsync(code, "Run()", Spec("Title", "string", tweak: s => s.Summary = "The title."));

        Assert.False(plan.Refused);
        Assert.Contains("    public string Name { get; set; }\n\n    /// <summary>The title.</summary>\n    public string Title { get; set; }\n\n    public void Run()", await TextAsync(plan));
    }

    [Fact]
    public async Task ADocumentedTypeWithoutASummaryGetsAWarningNotAnInventedComment()
    {
        const string code = "class C\n{\n    /// <summary>The name.</summary>\n    public string Name { get; set; }\n}\n";

        var plan = await AddAsync(code, "Name {", Spec("Title", "string"));

        Assert.False(plan.Refused);
        Assert.Contains(plan.Warnings, w => w.Contains("Title", StringComparison.Ordinal));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(await TextAsync(plan), "<summary>"));
    }

    [Fact]
    public async Task MembersAreAddedBeforeAnEditedMethodAndItsLinesStillLineUp()
    {
        const string code = "class C\n{\n    public int Add(int a, int b) { return a + b; }\n}\n";
        var (line, column) = MethodEditEngineTests.Locate(code, "Add(int");
        var solution = MethodEditEngineTests.Create(code);

        var plan = await MemberPlanning.WithMembersAsync(solution, File, line, [Spec("_calls", "int", "private", "field-only")],
            (s, shift, baseline) => MethodEditEngine.PlanAsync(
                s, File, shift(line), column, "public int Add(int a, int b) { _calls++; return a + b; }", Array.Empty<CallerEdit>(), null, baseline));

        Assert.False(plan.Refused);
        Assert.Equal(["_calls"], plan.MembersAdded);
        var text = await TextAsync(plan);
        Assert.Contains("private int _calls;", text);
        Assert.Contains("_calls++;", text);
    }

    // ---- editing ----

    [Fact]
    public async Task AttributesCanBeAddedAndRemovedWithoutTouchingReferences()
    {
        const string code =
            "class MarkerAttribute : System.Attribute { }\nclass RequiredAttribute : System.Attribute { }\n" +
            "class C\n{\n    [Marker]\n    public string Name { get; set; }\n    public void Use() { Name = \"x\"; }\n}\n";
        var edit = new MemberEdit();
        edit.AddAttributes.Add("Required");
        edit.RemoveAttributes.Add("Marker");

        var plan = await EditAsync(code, "Name {", edit);

        Assert.False(plan.Refused);
        Assert.False(plan.SignatureChanged);
        var text = await TextAsync(plan);
        Assert.Contains("[Required]", text);
        Assert.DoesNotContain("[Marker]", text);
    }

    private const string Counter =
        "class C\n" +
        "{\n" +
        "    public int Count { get; set; }\n" +
        "    public void Use() { Count = 5; }\n" +
        "}\n";

    [Fact]
    public async Task ChangingTheTypeOfAUsedMemberNeedsEveryReferenceCovered()
    {
        var plan = await EditAsync(Counter, "Count {", new MemberEdit { NewType = "long" });

        Assert.True(plan.Refused);
        Assert.True(plan.SignatureChanged);
        Assert.Single(plan.Uncovered);
    }

    [Fact]
    public async Task ChangingTheTypeIsAllowedWhenTheReferenceHasACallerEdit()
    {
        var (line, column) = MethodEditEngineTests.Locate(Counter, "Count = 5");

        var plan = await EditAsync(Counter, "Count {", new MemberEdit { NewType = "long" }, new CallerEdit(File, line, column, "Count"));

        Assert.False(plan.Refused);
        Assert.Contains("public long Count { get; set; }", await TextAsync(plan));
    }

    [Fact]
    public async Task NarrowingAPrivateMembersAccessibilityStillChecksItsReferences()
    {
        const string code = "class C\n{\n    private int _n;\n    public void Use() { _n = 5; }\n}\n";

        var plan = await EditAsync(code, "_n;", new MemberEdit { Accessibility = "private", NewType = "long" });

        Assert.True(plan.Refused);
        Assert.Single(plan.Uncovered);
    }

    [Fact]
    public async Task NarrowingPublicToPrivateIsRefusedWhenAnotherClassUsesIt()
    {
        const string code = "class C\n{\n    public int Count { get; set; }\n}\nclass D\n{\n    public int Read(C c) { return c.Count; }\n}\n";

        var plan = await EditAsync(code, "Count {", new MemberEdit { Accessibility = "private" });

        Assert.True(plan.Refused);
        Assert.NotEmpty(plan.Uncovered);
    }

    [Fact]
    public async Task ASetterCanBeMadePrivateWhenNothingOutsideSetsIt()
    {
        const string code = "class C\n{\n    public int Count { get; set; }\n}\n";

        var plan = await EditAsync(code, "Count {", new MemberEdit { Setter = "private set" });

        Assert.False(plan.Refused);
        Assert.Contains("public int Count { get; private set; }", await TextAsync(plan));
    }

    [Fact]
    public async Task ARenameUpdatesEveryReferenceWithoutCallerEdits()
    {
        var plan = await EditAsync(Counter, "Count {", new MemberEdit { NewName = "Total" });

        Assert.False(plan.Refused);
        var text = await TextAsync(plan);
        Assert.Contains("public int Total { get; set; }", text);
        Assert.Contains("Total = 5;", text);
        Assert.DoesNotContain("Count", text);
    }

    [Fact]
    public async Task ARenameCombinedWithATypeChangeIsRefused()
    {
        var plan = await EditAsync(Counter, "Count {", new MemberEdit { NewName = "Total", NewType = "long" });

        Assert.True(plan.Refused);
    }

    [Fact]
    public async Task AnAutoPropertyCanBeBackedByAField()
    {
        const string code = "class C\n{\n    public int Count { get; set; } = 3;\n}\n";

        var plan = await EditAsync(code, "Count {", new MemberEdit { Backing = "field" });

        Assert.False(plan.Refused);
        var text = await TextAsync(plan);
        Assert.Contains("private int _count = 3;", text);
        Assert.Contains("get => _count;", text);
    }

    [Fact]
    public async Task APropertyOverAFieldCanBecomeAnAutoPropertyWhenTheFieldIsUsedNowhereElse()
    {
        const string code = "class C\n{\n    private int _count;\n    public int Count { get => _count; set => _count = value; }\n}\n";

        var plan = await EditAsync(code, "Count {", new MemberEdit { Backing = "auto" });

        Assert.False(plan.Refused);
        var text = await TextAsync(plan);
        Assert.Contains("public int Count { get; set; }", text);
        Assert.DoesNotContain("_count", text);
    }

    [Fact]
    public async Task AFieldUsedElsewhereIsNotFoldedIntoAnAutoProperty()
    {
        const string code = "class C\n{\n    private int _count;\n    public int Count { get => _count; set => _count = value; }\n    public void Reset() { _count = 0; }\n}\n";

        var plan = await EditAsync(code, "Count {", new MemberEdit { Backing = "auto" });

        Assert.True(plan.Refused);
    }

    [Fact]
    public async Task EditingAMethodAsAMemberIsRefused()
    {
        var plan = await EditAsync(Counter, "Use()", new MemberEdit { NewType = "long" });

        Assert.True(plan.Refused);
    }

    [Fact]
    public async Task ANewSummaryReplacesTheMembersDocSummary()
    {
        const string code = "class C\n{\n    /// <summary>Old.</summary>\n    public int Count { get; set; }\n}\n";

        var plan = await EditAsync(code, "Count {", new MemberEdit { NewSummary = "How many." });

        Assert.False(plan.Refused);
        Assert.Contains("/// <summary>How many.</summary>", await TextAsync(plan));
        Assert.True(plan.DocsUpdated);
    }
}
