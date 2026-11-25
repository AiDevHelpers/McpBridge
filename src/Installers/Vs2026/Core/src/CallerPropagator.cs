using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Walks callers of a method up to a depth: the methods that call it, the methods that call those, and so on. Used by the change tools
/// that must update a chain of callers (thread a parameter through, make a method async). A method is visited once, so recursion ends.
/// </summary>
internal static class CallerPropagator
{
    /// <summary>
    /// The methods that reach <paramref name="method"/> in up to <paramref name="depth"/> steps, nearest first. Depth 1 is the direct
    /// callers. Each entry is the method's symbol; a caller that is a lambda or a field initializer is reported by its containing method.
    /// </summary>
    public static async Task<List<IMethodSymbol>> CallersAsync(Solution solution, IMethodSymbol method, int depth)
    {
        var found = new List<IMethodSymbol>();
        var seen = new HashSet<string>(System.StringComparer.Ordinal) { Key(method) };
        var frontier = new List<IMethodSymbol> { method };
        for (var level = 0; level < depth && frontier.Count > 0; level++)
        {
            var next = new List<IMethodSymbol>();
            foreach (var target in frontier)
            {
                foreach (var caller in await DirectCallersAsync(solution, target).ConfigureAwait(false))
                {
                    if (seen.Add(Key(caller)))
                    {
                        found.Add(caller);
                        next.Add(caller);
                    }
                }
            }

            frontier = next;
        }

        return found;
    }

    private static async Task<IEnumerable<IMethodSymbol>> DirectCallersAsync(Solution solution, IMethodSymbol method)
    {
        var callers = new List<IMethodSymbol>();
        foreach (var referenced in await SymbolFinder.FindReferencesAsync(method, solution).ConfigureAwait(false))
        {
            foreach (var location in referenced.Locations)
            {
                var root = await location.Document.GetSyntaxRootAsync().ConfigureAwait(false);
                var model = await location.Document.GetSemanticModelAsync().ConfigureAwait(false);
                var node = root?.FindToken(location.Location.SourceSpan.Start).Parent?.AncestorsAndSelf()
                    .OfType<MethodDeclarationSyntax>().FirstOrDefault();
                var symbol = node is null || model is null ? null : model.GetDeclaredSymbol(node);
                if (symbol is not null)
                {
                    callers.Add(symbol);
                }
            }
        }

        return callers;
    }

    internal static string Key(IMethodSymbol method) => method.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
}
