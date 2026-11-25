using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>One parameter of the changed signature, in its new position. <see cref="FromOld"/> is the index of the old parameter it keeps
/// (null for a new parameter), and <see cref="Value"/> is the argument a new parameter gives existing callers.</summary>
internal sealed class NewParameter
{
    public NewParameter(string declaration, int? fromOld, string? value)
    {
        Declaration = declaration;
        FromOld = fromOld;
        Value = value;
    }

    /// <summary>The parameter as written in the new signature, such as <c>int b</c> or <c>CancellationToken ct = default</c>.</summary>
    public string Declaration { get; }

    public int? FromOld { get; }

    public string? Value { get; }
}

/// <summary>
/// Rewrites the invocations of a method so each call passes the arguments of the new signature: arguments move with their parameters,
/// removed parameters' arguments are dropped (only when they cannot have side effects), and new parameters get their value. Named
/// arguments are matched by name. Calls it cannot rewrite safely (method groups, delegates, constructors, attributes, <c>params</c>
/// expansion) are returned as uncovered, so the change is refused until a person covers them.
/// </summary>
internal static class CallSiteRewriter
{
    public static async Task<(List<CallerEdit> Edits, List<object> Uncovered)> RewriteAsync(
        Solution solution, IMethodSymbol method, IReadOnlyList<MethodEditEngine.Reference> references, IReadOnlyList<NewParameter> newParameters)
    {
        var edits = new List<CallerEdit>();
        var uncovered = new List<object>();
        foreach (var reference in references)
        {
            var document = solution.GetDocument(reference.DocumentId);
            var root = document is null ? null : await document.GetSyntaxRootAsync().ConfigureAwait(false);
            if (root is null)
            {
                continue;
            }

            var token = root.FindToken(reference.Span.Start);
            var invocation = MethodEditEngine.UseNode(token) as InvocationExpressionSyntax;
            if (invocation is null || token.Parent is null)
            {
                uncovered.Add(Uncovered(reference, Strings.Get("CallSiteNotPlainCall")));
                continue;
            }

            if (method.Parameters.Any(p => p.IsParams))
            {
                uncovered.Add(Uncovered(reference, Strings.Get("CallSiteParams")));
                continue;
            }

            var arguments = invocation.ArgumentList.Arguments;
            var old = new ArgumentSyntax?[method.Parameters.Length];
            var positional = 0;
            var refusal = (string?)null;
            foreach (var argument in arguments)
            {
                int index;
                if (argument.NameColon is { } named)
                {
                    index = method.Parameters.ToList().FindIndex(p => p.Name == named.Name.Identifier.ValueText);
                }
                else
                {
                    index = positional++;
                }

                if (index < 0 || index >= old.Length)
                {
                    refusal = Strings.Get("CallSiteArgumentMismatch");
                    break;
                }

                old[index] = argument;
            }

            if (refusal is null)
            {
                var args = new List<string>();
                for (var k = 0; k < newParameters.Count && refusal is null; k++)
                {
                    var keep = newParameters[k].FromOld;
                    if (keep is { } j)
                    {
                        var argument = old[j];
                        if (argument is null)
                        {
                            refusal = Strings.Get("CallSiteKeptParameterMissing");
                            break;
                        }

                        args.Add(ArgumentText(argument));
                    }
                    else if (newParameters[k].Value is { } value)
                    {
                        args.Add(value);
                    }
                    else
                    {
                        refusal = Strings.Get("CallSiteNewParameterNoValue");
                    }
                }

                for (var j = 0; j < old.Length && refusal is null; j++)
                {
                    if (newParameters.Any(p => p.FromOld == j) || old[j] is null)
                    {
                        continue;
                    }

                    if (!IsSideEffectFree(old[j]!.Expression))
                    {
                        refusal = Strings.Get("CallSiteRemovedArgumentSideEffects");
                    }
                }

                if (refusal is null)
                {
                    var line = reference.Line;
                    var column = reference.Column;
                    var callText = invocation.Expression.WithoutTrivia().ToString() + "(" + string.Join(", ", args) + ")";
                    edits.Add(new CallerEdit(reference.File, line, column, callText));
                    continue;
                }
            }

            uncovered.Add(Uncovered(reference, refusal!));
        }

        return (edits, uncovered);
    }

    private static string ArgumentText(ArgumentSyntax argument)
    {
        var prefix = argument.RefKindKeyword.IsKind(SyntaxKind.None) ? "" : argument.RefKindKeyword.ValueText + " ";
        return prefix + argument.Expression.ToString();
    }

    // Literals, names and defaults cannot change anything when dropped; anything else is refused.
    private static bool IsSideEffectFree(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax or IdentifierNameSyntax or DefaultExpressionSyntax or ThisExpressionSyntax
        || expression is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax or IdentifierNameSyntax };

    private static object Uncovered(MethodEditEngine.Reference reference, string reason) => new
    {
        file = reference.File,
        line = reference.Line,
        column = reference.Column,
        reason,
    };
}
