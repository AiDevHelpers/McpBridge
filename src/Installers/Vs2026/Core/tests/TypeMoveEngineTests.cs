using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class TypeMoveEngineTests
{
    private const string WidgetFile = @"C:\proj\Widget.cs";
    private const string SiblingFile = @"C:\proj\Sibling.cs";
    private const string UserFile = @"C:\proj\User.cs";
    private const string OnlyFile = @"C:\proj\Only.cs";

    private const string Widget = "namespace Old.Ns\n{\n    public class Widget { Sibling s; }\n}\n";
    private const string Sibling = "namespace Old.Ns\n{\n    public class Sibling { Widget w; }\n}\n";
    private const string User = "using Old.Ns;\nnamespace App\n{\n    class User\n    {\n        Widget a;\n        Old.Ns.Widget b;\n        Sibling s;\n    }\n}\n";
    private const string Only = "using Old.Ns;\nnamespace Z\n{\n    class P { Widget w; }\n}\n";

    private static Task<MethodEditPlan> MoveAsync(IEnumerable<(string, string)> files, string file, string marker, string newNamespace, params CallerEdit[] callerEdits)
    {
        var text = files.First(f => f.Item1 == file).Item2;
        var (line, _) = MethodEditEngineTests.Locate(text, marker);
        return TypeMoveEngine.MoveAsync(NamespaceEngineTests.Multi(files), file, line, newNamespace, false, callerEdits);
    }

    private static readonly (string, string)[] AllFiles = [(WidgetFile, Widget), (SiblingFile, Sibling), (UserFile, User), (OnlyFile, Only)];

    [Fact]
    public async Task MovingATypeFixesItsDeclarationItsQualifiedUsesAndTheUsingsAroundIt()
    {
        var plan = await MoveAsync(AllFiles, WidgetFile, "class Widget", "New.Core");

        Assert.False(plan.Refused);
        var widget = await NamespaceEngineTests.TextOfAsync(plan, WidgetFile);
        Assert.Contains("namespace New.Core", widget);
        Assert.Contains("using Old.Ns;", widget);

        Assert.Contains("using New.Core;", await NamespaceEngineTests.TextOfAsync(plan, SiblingFile));

        var user = await NamespaceEngineTests.TextOfAsync(plan, UserFile);
        Assert.Contains("using New.Core;", user);
        Assert.Contains("using Old.Ns;", user);
        Assert.Contains("New.Core.Widget b;", user);

        var only = await NamespaceEngineTests.TextOfAsync(plan, OnlyFile);
        Assert.Contains("using New.Core;", only);
        Assert.DoesNotContain("using Old.Ns;", only);
    }

    [Fact]
    public async Task AFileThatDeclaresOtherTypesIsRefused()
    {
        var shared = "namespace Old.Ns\n{\n    public class Widget { }\n    public class Gadget { }\n}\n";

        var plan = await MoveAsync([(WidgetFile, shared)], WidgetFile, "class Widget", "New.Core");

        Assert.True(plan.Refused);
        Assert.Contains(plan.Reasons, r => r.Contains("Gadget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATypeThatAlreadyExistsInTheTargetIsRefused()
    {
        var other = "namespace New.Core\n{\n    public class Widget { }\n}\n";

        var plan = await MoveAsync([(WidgetFile, "namespace Old.Ns\n{\n    public class Widget { }\n}\n"), (SiblingFile, other)], WidgetFile, "class Widget", "New.Core");

        Assert.True(plan.Refused);
    }

    [Fact]
    public async Task ANestedTypeAndATypeInTheGlobalNamespaceAreRefused()
    {
        var nested = "namespace Old.Ns\n{\n    public class Outer\n    {\n        public class Inner { }\n    }\n}\n";
        Assert.True((await MoveAsync([(WidgetFile, nested)], WidgetFile, "class Inner", "New.Core")).Refused);

        Assert.True((await MoveAsync([(WidgetFile, "public class Free { }\n")], WidgetFile, "class Free", "New.Core")).Refused);
    }

    [Fact]
    public async Task MovingToTheSameNamespaceOrABadOneIsRefused()
    {
        Assert.True((await MoveAsync([(WidgetFile, "namespace Old.Ns\n{\n    public class Widget { }\n}\n")], WidgetFile, "class Widget", "Old.Ns")).Refused);
        Assert.True((await MoveAsync([(WidgetFile, "namespace Old.Ns\n{\n    public class Widget { }\n}\n")], WidgetFile, "class Widget", "New..Core")).Refused);
    }

    [Fact]
    public async Task ARelativeQualifiedUseIsRefusedUnlessACallerEditCoversIt()
    {
        var relative = "namespace Old\n{\n    class X { Ns.Widget w; }\n}\n";
        var files = new[] { (WidgetFile, "namespace Old.Ns\n{\n    public class Widget { }\n}\n"), (UserFile, relative) };

        var refused = await MoveAsync(files, WidgetFile, "class Widget", "New.Core");
        Assert.True(refused.Refused);

        var (line, column) = MethodEditEngineTests.Locate(relative, "Widget w");
        var covered = await MoveAsync(files, WidgetFile, "class Widget", "New.Core", new CallerEdit(UserFile, line, column, "New.Core"));
        Assert.False(covered.Refused);
    }

    [Fact]
    public async Task EveryPartOfAPartialTypeMoves()
    {
        var one = "namespace Old.Ns\n{\n    public partial class Big { }\n}\n";
        var two = "namespace Old.Ns\n{\n    public partial class Big { int x; }\n}\n";

        var plan = await MoveAsync([(WidgetFile, one), (SiblingFile, two)], WidgetFile, "class Big", "New.Core");

        Assert.False(plan.Refused);
        Assert.Contains("namespace New.Core", await NamespaceEngineTests.TextOfAsync(plan, WidgetFile));
        Assert.Contains("namespace New.Core", await NamespaceEngineTests.TextOfAsync(plan, SiblingFile));
        Assert.Equal(2, plan.DeclarationCount);
    }
}
