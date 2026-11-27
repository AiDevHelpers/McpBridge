using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

// Parity: every edit action the Visual Studio bridge handles (IdeActions.cs) is either answered by the helper or listed as
// unsupported with a reason. A new VS edit action goes into VsEditActions, and then here or in the helper.
public class VsParityTests
{
    private static readonly string[] VsEditActions =
    {
        "addEnumMember", "addInterfaceMember", "addMember", "addMethod", "addOverload", "addProjectItem", "addType",
        "changeSignature", "codeCleanup", "convert", "deleteMember", "deleteType", "editEnumMember", "editMember", "editMethod",
        "editType", "extractInterface", "extractMethod", "extractPartial", "extractValue", "formatDocument", "formatSolution",
        "generateConstructor", "generateMembers", "implementMembers", "inlineMethod", "listUnused", "mergePartial", "moveMember",
        "moveType", "organizeFile", "removeProjectItem", "removeUnused", "renameNamespace", "renameSymbol", "replaceInFiles",
        "tddAddTest", "tddUpdateTest", "threadParameter", "updateDocs",
    };

    // Edit actions the helper does not answer, with the reason. Each one is a decision, not a leftover.
    private static readonly Dictionary<string, string> Unsupported = new(StringComparer.Ordinal)
    {
        ["addProjectItem"] = "Project file edits stay in Visual Studio; the helper does not edit .csproj files.",
        ["removeProjectItem"] = "Project file edits stay in Visual Studio; the helper does not edit .csproj files.",
        ["codeCleanup"] = "Runs the IDE's cleanup profile; not in the helper's v1 units.",
        ["formatDocument"] = "Runs the IDE formatter; not in the helper's v1 units.",
        ["formatSolution"] = "Runs the IDE formatter over the solution; not in the helper's v1 units.",
        ["replaceInFiles"] = "Text search and replace across files; not in the helper's v1 units.",
        ["convert"] = "Not in the helper's v1 units; add a unit if the user asks.",
        ["extractInterface"] = "Not in the helper's v1 units; add a unit if the user asks.",
        ["extractMethod"] = "Not in the helper's v1 units; add a unit if the user asks.",
        ["extractPartial"] = "Not in the helper's v1 units; add a unit if the user asks.",
        ["extractValue"] = "Not in the helper's v1 units; add a unit if the user asks.",
        ["generateMembers"] = "Not in the helper's v1 units; add a unit if the user asks.",
        ["inlineMethod"] = "Not in the helper's v1 units; add a unit if the user asks.",
        ["mergePartial"] = "Not in the helper's v1 units; add a unit if the user asks.",
        ["moveMember"] = "Not in the helper's v1 units; add a unit if the user asks.",
        ["organizeFile"] = "Not in the helper's v1 units; add a unit if the user asks.",
        ["removeUnused"] = "Not in the helper's v1 units; add a unit if the user asks.",
        ["updateDocs"] = "Not in the helper's v1 units; add a unit if the user asks.",
    };

    [Fact]
    public void every_vs_edit_action_is_handled_by_the_helper_or_listed_as_unsupported()
    {
        var handler = new RoslynRequestHandler(() => null);

        var missing = VsEditActions.Where(a => !handler.HandlesAction(a) && !Unsupported.ContainsKey(a)).ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void an_unsupported_action_is_not_also_handled_and_is_a_known_edit_action()
    {
        var handler = new RoslynRequestHandler(() => null);

        Assert.Empty(Unsupported.Keys.Where(handler.HandlesAction).ToList());
        Assert.Empty(Unsupported.Keys.Where(k => !VsEditActions.Contains(k)).ToList());
    }
}
