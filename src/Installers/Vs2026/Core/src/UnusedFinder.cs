using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Finds private and internal members and types with no references, for review before deletion (<c>ide_delete_member</c>,
/// <c>ide_delete_type</c>). Read only. Public members are reported only when asked, since other assemblies may use them. A member with no
/// reference can still be used through reflection, serialization, dependency injection or a string name: those are reported as low
/// confidence, so a person decides.
/// </summary>
internal static class UnusedFinder
{
    public static async Task<List<object>> FindAsync(Solution solution, string? projectName, bool includePublic)
    {
        var found = new List<object>();
        foreach (var project in solution.Projects.Where(p => projectName is null || string.Equals(p.Name, projectName, System.StringComparison.OrdinalIgnoreCase)))
        {
            var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            foreach (var type in AllTypes(compilation.Assembly.GlobalNamespace))
            {
                if (IsCandidate(type, includePublic) && !await HasReferencesAsync(solution, type).ConfigureAwait(false))
                {
                    found.Add(Entry(project, type, "type", LowConfidence(type)));
                }

                foreach (var member in type.GetMembers().Where(m => !m.IsImplicitlyDeclared && IsMemberCandidate(m, includePublic)))
                {
                    if (!await HasReferencesAsync(solution, member).ConfigureAwait(false))
                    {
                        found.Add(Entry(project, member, member.Kind.ToString().ToLowerInvariant(), LowConfidence(member)));
                    }
                }
            }
        }

        return found;
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in Nested(type))
            {
                yield return nested;
            }
        }

        foreach (var child in ns.GetNamespaceMembers())
        {
            foreach (var type in AllTypes(child))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in Nested(nested))
            {
                yield return deeper;
            }
        }
    }

    private static bool IsCandidate(INamedTypeSymbol type, bool includePublic) =>
        !type.IsImplicitlyDeclared && (includePublic || type.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal or Accessibility.ProtectedAndInternal or Accessibility.Protected or Accessibility.NotApplicable)
        && type.Locations.Any(l => l.IsInSource) && !type.IsStatic;

    private static bool IsMemberCandidate(ISymbol member, bool includePublic) =>
        (member is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.Constructor } or IPropertySymbol or IFieldSymbol or IEventSymbol)
        && member.Locations.Any(l => l.IsInSource)
        && (includePublic || member.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal or Accessibility.ProtectedAndInternal or Accessibility.Protected);

    private static async Task<bool> HasReferencesAsync(Solution solution, ISymbol symbol)
    {
        var refs = await SymbolFinder.FindReferencesAsync(symbol, solution).ConfigureAwait(false);
        return refs.Any(r => r.Locations.Any(l => l.Location.IsInSource && !symbol.Locations.Contains(l.Location)));
    }

    // Reflection, serialization, DI and string names reach a member without a reference. Attributes and interfaces are the usual signs.
    private static bool LowConfidence(ISymbol symbol) =>
        symbol.GetAttributes().Any() || (symbol is INamedTypeSymbol t && t.AllInterfaces.Any()) || symbol is IMethodSymbol { MethodKind: MethodKind.Constructor };

    private static object Entry(Project project, ISymbol symbol, string kind, bool low) => new
    {
        project = project.Name,
        kind,
        name = symbol.ToDisplayString(),
        file = symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath,
        line = symbol.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan().StartLinePosition.Line + 1,
        confidence = low ? "low: attributes, interfaces or constructors can be used without a reference" : "high",
    };
}
