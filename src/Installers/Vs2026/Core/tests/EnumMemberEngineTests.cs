using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class EnumMemberEngineTests
{
    private const string File = @"C:\scratch\enum.cs";

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task AMemberIsAddedAfterTheLast()
    {
        var solution = TestSolutions.Single([(File, "enum E\n{\n    A,\n    B,\n}\n")]);

        var plan = await EnumMemberEngine.AddAsync(solution, File, 1, "C", "3");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("C = 3,", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AValueOfAnInternalEnumChanges()
    {
        var solution = TestSolutions.Single([(File, "enum E\n{\n    A,\n    B,\n}\n")]);

        var plan = await EnumMemberEngine.EditValueAsync(solution, File, 3, "5", false);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("A = 5,", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AValueOfAPublicEnumNeedsAllowValueChange()
    {
        var solution = TestSolutions.Single([(File, "public enum E\n{\n    A,\n}\n")]);

        var refused = await EnumMemberEngine.EditValueAsync(solution, File, 3, "5", false);
        var allowed = await EnumMemberEngine.EditValueAsync(solution, File, 3, "5", true);

        Assert.True(refused.Refused);
        Assert.Contains(refused.Reasons, r => r.Contains("allowValueChange", System.StringComparison.Ordinal));
        Assert.False(allowed.Refused, string.Join("; ", allowed.Reasons));
    }

    [Fact]
    public async Task AValueOfAFlagsEnumNeedsAllowValueChange()
    {
        var solution = TestSolutions.Single([(File, "[System.Flags]\nenum E\n{\n    A = 1,\n}\n")]);

        var plan = await EnumMemberEngine.EditValueAsync(solution, File, 4, "2", false);

        Assert.True(plan.Refused);
    }
}
