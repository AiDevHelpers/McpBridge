using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// The "every reference is covered, and related members are refused" rule, for symbols of any member kind. Used by the method, member
/// and (later) type and signature engines, so each one reports the same uncovered references and the same related members.
/// </summary>
internal static class CoverageOfReferences
{
    /// <summary>
    /// The references that no caller edit covers. A caller edit covers a reference when it names the same file, line and column the
    /// reference search reported.
    /// </summary>
    public static List<object> Uncovered(IEnumerable<MethodEditEngine.Reference> references, IReadOnlyList<CallerEdit> callerEdits) =>
        references
            .Where(r => !callerEdits.Any(e => string.Equals(e.File, r.File, System.StringComparison.OrdinalIgnoreCase) && e.Line == r.Line && e.Column == r.Column))
            .Select(r => (object)new { file = r.File, line = r.Line, column = r.Column, implicit_use = r.IsImplicit, candidate = r.IsCandidate })
            .ToList();

    /// <summary>
    /// The members a change to <paramref name="symbol"/> would also change: what it overrides, what it implements, and what overrides it.
    /// A change to any of them is refused by the engines, so the list is what the user must change first. Empty for a symbol with none.
    /// </summary>
    public static async Task<List<string>> RelatedAsync(Solution solution, ISymbol symbol)
    {
        var related = new List<string>();

        switch (symbol)
        {
            case IMethodSymbol method:
                if (method.OverriddenMethod is not null)
                {
                    related.Add(method.OverriddenMethod.ToDisplayString());
                }

                related.AddRange(ImplementedBy(method.ContainingType, method));
                break;
            case IPropertySymbol property:
                if (property.OverriddenProperty is not null)
                {
                    related.Add(property.OverriddenProperty.ToDisplayString());
                }

                related.AddRange(ImplementedBy(property.ContainingType, property));
                break;
            case IEventSymbol evt:
                if (evt.OverriddenEvent is not null)
                {
                    related.Add(evt.OverriddenEvent.ToDisplayString());
                }

                related.AddRange(ImplementedBy(evt.ContainingType, evt));
                break;
        }

        if (symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride)
        {
            foreach (var overrider in await SymbolFinder.FindOverridesAsync(symbol, solution).ConfigureAwait(false))
            {
                related.Add(overrider.ToDisplayString());
            }
        }

        return related.Distinct().ToList();
    }

    // Interface members of the type's interfaces that this member implements.
    private static IEnumerable<string> ImplementedBy(INamedTypeSymbol type, ISymbol member)
    {
        foreach (var iface in type.AllInterfaces)
        {
            foreach (var interfaceMember in iface.GetMembers(member.Name))
            {
                if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(interfaceMember), member))
                {
                    yield return interfaceMember.ToDisplayString();
                }
            }
        }
    }
}
