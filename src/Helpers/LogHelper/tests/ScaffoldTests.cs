using System.Reflection;
using Xunit;

namespace AiDevHelpers.Helpers.LogHelper.Tests;

public sealed class ScaffoldTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "Microsoft.AspNetCore",
        "EnvDTE",
        "AiDevHelpers.MCP.Core",
        "AiDevHelpers.Common",
        "AiDevHelpers.Bridge",
    ];

    [Fact]
    public void Assembly_HasNoAspNetIdeMcpOrCommonReferences()
    {
        var assembly = Assembly.Load("AiDevHelpers.Helpers.LogHelper");

        var names = assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();

        foreach (var prefix in ForbiddenPrefixes)
        {
            Assert.DoesNotContain(names, n => n.StartsWith(prefix, StringComparison.Ordinal));
        }
    }
}
