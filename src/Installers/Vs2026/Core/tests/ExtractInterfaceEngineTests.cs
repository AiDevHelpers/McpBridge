using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class ExtractInterfaceEngineTests
{
    private const string File = @"C:\scratch\Store.cs";

    private static async Task<string> TextOfAsync(MethodEditPlan plan, string name) =>
        (await plan.Result!.Projects.Single().Documents.Single(d => d.Name == name).GetTextAsync()).ToString();

    [Fact]
    public async Task AnInterfaceIsWrittenBesideTheTypeAndImplemented()
    {
        const string code = "class Store\n{\n    public int Count() => 0;\n    public string Name { get; set; }\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ExtractInterfaceEngine.PlanAsync(solution, File, 1, "Store", ["Count", "Name"]);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var type = await TextOfAsync(plan, "Store.cs");
        Assert.Contains("class Store : IStore", type);
        var iface = await TextOfAsync(plan, "IStore.cs");
        Assert.Contains("public interface IStore", iface);
        Assert.Contains("int Count();", iface);
        Assert.Contains("string Name { get; set; }", iface);
    }

    [Fact]
    public async Task APrivateMemberIsRefused()
    {
        const string code = "class Store\n{\n    int Secret() => 0;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ExtractInterfaceEngine.PlanAsync(solution, File, 1, "Store", ["Secret"]);

        Assert.True(plan.Refused);
    }
}
