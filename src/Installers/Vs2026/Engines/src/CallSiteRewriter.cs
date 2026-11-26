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
        HasDefault = SyntaxFactory.ParseParameterList("(" + declaration + ")").Parameters.FirstOrDefault()?.Default is not null;
    }

    /// <summary>The parameter as written in the new signature, such as <c>int b</c> or <c>CancellationToken ct = default</c>.</summary>
    public string Declaration { get; }

    public int? FromOld { get; }

    public string? Value { get; }

    /// <summary>True when the declaration has a default, so a call may leave the parameter out.</summary>
    public bool HasDefault { get; }
}

/// <summary>
/// Rewrites the calls of a method so each passes the arguments of the new signature: arguments move with their parameters, removed
/// parameters' arguments are dropped (only when they cannot have side effects), new parameters get their value, and a trailing
/// argument left out for a defaulted parameter stays left out. Named arguments are matched by name. Covers method calls (including
/// extension calls, where the receiver is the first parameter), <c>new T(...)</c>, <c>base(...)</c> and <c>this(...)</c> initializers, and
/// attributes. Calls it cannot rewrite safely (method groups, delegates, <c>params</c> expansion, attribute named properties) are
/// returned as uncovered, so the change is refused until a person covers them.
/// </summary>
internal static class CallSiteRewriter
{
    /// <summary>The call a reference token names: a method call, an object creation, a constructor initializer or an attribute. Null when
    /// the token is something else (a method group, a nameof, a plain name).</summary>
    internal static SyntaxNode? CallOf(SyntaxToken token)
    {
        for (var node = token.Parent; node is not null && node is not StatementSyntax && node is not MemberDeclarationSyntax; node = node.Parent)
        {
            switch (node)
            {
                case InvocationExpressionSyntax invocation when invocation.Expression.Span.End == token.Span.End:
                    return invocation;
                case ObjectCreationExpressionSyntax creation when creation.Type.Span.End == token.Span.End:
                    return creation;
                case ConstructorInitializerSyntax initializer when initializer.ThisOrBaseKeyword.Span == token.Span:
                    return initializer;
                case AttributeSyntax attribute when attribute.Name.Span.Contains(token.Span):
                    return attribute;
            }
        }

        return null;
    }

    public static async Task<(List<CallerEdit> Edits, List<object> Uncovered)> RewriteAsync(
        Solution solution, IMethodSymbol method, IReadOnlyList<MethodEditEngine.Reference> references, IReadOnlyList<NewParameter> newParameters)
    {
        var edits = new List<CallerEdit>();
        var uncovered = new List<object>();
        foreach (var reference in references)
        {
            var document = solution.GetDocument(reference.DocumentId);
            var root = document is null ? null : await document.GetSyntaxRootAsync().ConfigureAwait(false);
            if (document is null || root is null)
            {
                continue;
            }

            var token = root.FindToken(reference.Span.Start);
            var call = CallOf(token);
            var parts = call is null ? null : ArgumentsOf(call);
            if (call is null || parts is null)
            {
                uncovered.Add(Uncovered(reference, Strings.Get("CallSiteNotPlainCall")));
                continue;
            }

            // A trailing params array: its arguments are passed one by one, or as the array itself.
            var paramsIndex = method.Parameters.Length > 0 && method.Parameters[method.Parameters.Length - 1].IsParams ? method.Parameters.Length - 1 : -1;
            if (method.Parameters.Take(Math.Max(0, method.Parameters.Length - 1)).Any(p => p.IsParams))
            {
                uncovered.Add(Uncovered(reference, Strings.Get("CallSiteParams")));
                continue;
            }

            // An extension method called as receiver.Method(...) passes the receiver as its first parameter.
            var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
            var reduced = model is not null && call is InvocationExpressionSyntax invocation
                && model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { ReducedFrom: not null }
                && invocation.Expression is MemberAccessExpressionSyntax;

            var (argumentList, arguments) = parts.Value;
            var old = new Arg?[method.Parameters.Length];
            if (reduced)
            {
                old[0] = new Arg(null, ((MemberAccessExpressionSyntax)((InvocationExpressionSyntax)call).Expression).Expression);
            }

            var refusal = (string?)null;
            var positional = reduced ? 1 : 0;
            var tail = new List<Arg>();
            foreach (var argument in arguments)
            {
                int index;
                if (argument.Name is { } name)
                {
                    index = method.Parameters.ToList().FindIndex(p => p.Name == name);
                    if (index >= 0 && index == paramsIndex)
                    {
                        refusal = Strings.Get("CallSiteParams");
                        break;
                    }
                }
                else if (paramsIndex >= 0 && positional >= paramsIndex)
                {
                    tail.Add(argument);
                    positional++;
                    continue;
                }
                else
                {
                    index = positional++;
                }

                if (index < 0 || index >= old.Length || old[index] is not null)
                {
                    refusal = Strings.Get("CallSiteArgumentMismatch");
                    break;
                }

                old[index] = argument;
            }

            // One argument that is already an array is the array itself; anything else is expanded arguments of the params array.
            if (refusal is null && paramsIndex >= 0 && tail.Count == 1 && model!.GetTypeInfo(tail[0].Expression).Type is IArrayTypeSymbol)
            {
                old[paramsIndex] = tail[0];
                tail.Clear();
            }

            if (refusal is null && paramsIndex >= 0 && newParameters.Select((p, i) => (p, i)).Any(t => t.p.FromOld == paramsIndex
                && newParameters.Skip(t.i + 1).Any()))
            {
                refusal = Strings.Get("CallSiteParams");
            }

            var output = new List<string>();
            if (refusal is null && reduced && (newParameters.Count == 0 || newParameters[0].FromOld != 0))
            {
                refusal = Strings.Get("CallSiteReceiverMoved");
            }

            var omitted = false;
            for (var k = 0; k < newParameters.Count && refusal is null; k++)
            {
                var parameter = newParameters[k];
                if (parameter.FromOld is { } j)
                {
                    if (j == paramsIndex)
                    {
                        if (omitted && (old[j] is not null || tail.Count > 0))
                        {
                            refusal = Strings.Get("CallSiteKeptParameterMissing");
                            break;
                        }

                        output.AddRange(old[j] is { } array ? new[] { array.Text } : tail.Select(t => t.Text).ToArray());
                        continue;
                    }

                    if (reduced && j == 0)
                    {
                        continue; // the receiver stays in the expression; checked to be first above
                    }

                    var argument = old[j];
                    if (argument is null)
                    {
                        if (method.Parameters[j].HasExplicitDefaultValue)
                        {
                            omitted = true;
                            continue;
                        }

                        refusal = Strings.Get("CallSiteKeptParameterMissing");
                        break;
                    }

                    if (omitted)
                    {
                        refusal = Strings.Get("CallSiteKeptParameterMissing");
                        break;
                    }

                    output.Add(argument.Text);
                }
                else if (parameter.Value is { } value)
                {
                    if (omitted)
                    {
                        refusal = Strings.Get("CallSiteKeptParameterMissing");
                        break;
                    }

                    output.Add(value);
                }
                else if (parameter.HasDefault)
                {
                    omitted = true;
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

            // A removed params parameter drops its expanded arguments, which must have no side effects.
            if (refusal is null && paramsIndex >= 0 && !newParameters.Any(p => p.FromOld == paramsIndex)
                && tail.Any(t => !IsSideEffectFree(t.Expression)))
            {
                refusal = Strings.Get("CallSiteRemovedArgumentSideEffects");
            }

            if (refusal is null && argumentList is null)
            {
                // No parentheses: an empty argument list needs no edit, and arguments cannot be added without writing the parentheses.
                if (output.Count == 0)
                {
                    continue;
                }

                refusal = Strings.Get("CallSiteNoArgumentList");
            }

            if (refusal is null)
            {
                var text = call.ToString();
                var start = argumentList!.SpanStart - call.SpanStart;
                var end = argumentList.Span.End - call.SpanStart;
                var callText = text.Substring(0, start) + "(" + string.Join(", ", output) + ")" + text.Substring(end);
                edits.Add(new CallerEdit(reference.File, reference.Line, reference.Column, callText));
                continue;
            }

            uncovered.Add(Uncovered(reference, refusal!));
        }

        return (edits, uncovered);
    }

    // The argument list of a call (null when the call has no parentheses) and its arguments. Null for an attribute named property, which
    // is an assignment to a property, not a parameter.
    private static (SyntaxNode? List, List<Arg> Arguments)? ArgumentsOf(SyntaxNode call)
    {
        switch (call)
        {
            case InvocationExpressionSyntax invocation:
                return (invocation.ArgumentList, invocation.ArgumentList.Arguments.Select(Of).ToList());
            case ObjectCreationExpressionSyntax creation:
                return (creation.ArgumentList, creation.ArgumentList?.Arguments.Select(Of).ToList() ?? new List<Arg>());
            case ConstructorInitializerSyntax initializer:
                return (initializer.ArgumentList, initializer.ArgumentList.Arguments.Select(Of).ToList());
            case AttributeSyntax attribute:
                if (attribute.ArgumentList is null)
                {
                    return (null, new List<Arg>());
                }

                if (attribute.ArgumentList.Arguments.Any(a => a.NameEquals is not null))
                {
                    return null;
                }

                return (attribute.ArgumentList, attribute.ArgumentList.Arguments
                    .Select(a => new Arg(a.NameColon?.Name.Identifier.ValueText, a.Expression)).ToList());
            default:
                return null;
        }
    }

    private static Arg Of(ArgumentSyntax argument) =>
        new(argument.NameColon?.Name.Identifier.ValueText, argument.Expression, RefPrefix(argument));

    private static string RefPrefix(ArgumentSyntax argument) =>
        argument.RefKindKeyword.IsKind(SyntaxKind.None) ? "" : argument.RefKindKeyword.ValueText + " ";

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

    // One argument of a call: its name (null when positional), its text as written in a new call, and its expression.
    private sealed class Arg
    {
        public Arg(string? name, ExpressionSyntax expression, string prefix = "")
        {
            Name = name;
            Expression = expression;
            Text = prefix + expression.ToString();
        }

        public string? Name { get; }

        public ExpressionSyntax Expression { get; }

        public string Text { get; }
    }
}
