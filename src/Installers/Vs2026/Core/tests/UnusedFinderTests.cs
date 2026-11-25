using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class UnusedFinderTests
{
    private const string Code =
        "class C\n" +
        "{\n" +
        "    private int Unused() { return 1; }\n" +
        "    private int Used() { return 2; }\n" +
        "    private int Caller() { return Used(); }\n" +
        "    public int Api() { return 3; }\n" +
        "}\n";

    [Fact]
    public async Task APrivateMemberWithNoReferenceIsListed()
    {
        var found = await UnusedFinder.FindAsync(TestSolutions.Single([(@"C:\u.cs", Code)]), null, includePublic: false);

        var names = found.Select(f => System.Text.Json.JsonSerializer.Serialize(f)).ToList();
        Assert.Contains(names, n => n.Contains("C.Unused()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AUsedPrivateMemberIsNotListed()
    {
        var found = await UnusedFinder.FindAsync(TestSolutions.Single([(@"C:\u.cs", Code)]), null, includePublic: false);

        var names = found.Select(f => System.Text.Json.JsonSerializer.Serialize(f)).ToList();
        Assert.DoesNotContain(names, n => n.Contains("C.Used()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APublicMemberIsListedOnlyWhenAsked()
    {
        var solution = TestSolutions.Single([(@"C:\u.cs", Code)]);

        var without = await UnusedFinder.FindAsync(solution, null, includePublic: false);
        var with = await UnusedFinder.FindAsync(solution, null, includePublic: true);

        var withNames = with.Select(f => System.Text.Json.JsonSerializer.Serialize(f)).ToList();
        Assert.DoesNotContain(without.Select(f => System.Text.Json.JsonSerializer.Serialize(f)), n => n.Contains("C.Api()", StringComparison.Ordinal));
        Assert.Contains(withNames, n => n.Contains("C.Api()", StringComparison.Ordinal));
    }
}
