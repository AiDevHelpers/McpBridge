using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>The return type and accessibility a signature change sets. Null leaves that part as it is.</summary>
internal sealed class HeaderChange
{
    public string? ReturnType { get; set; }

    public string? Accessibility { get; set; }

    public bool IsEmpty => ReturnType is null && Accessibility is null;
}

/// <summary>
/// Changes the return type and accessibility of a method header as text. The other modifiers, the name and the body are kept.
/// Compile errors that a new return type or accessibility causes for callers are reported by the edit checks, which refuse them.
/// </summary>
internal static class ChangeSignatureHeader
{
    private static readonly string[] AccessWords = { "public", "internal", "protected", "private" };

    private static readonly string[] Accessibilities = { "public", "internal", "protected", "private", "protected internal", "private protected" };

    /// <summary>Why the header change cannot be written, or null when it can.</summary>
    public static string? Validate(HeaderChange header)
    {
        if (header.Accessibility is { } access && !Accessibilities.Contains(access))
        {
            return Strings.Format("ChangeSignatureBadHeader", access);
        }

        if (header.ReturnType is { } type && (type.Trim().Length == 0
            || SyntaxFactory.ParseTypeName(type).GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)))
        {
            return Strings.Format("ChangeSignatureBadHeader", type);
        }

        return null;
    }

    /// <summary>The text change that rewrites the modifiers and return type, or null when the header is unchanged.</summary>
    public static TextChange? TextChangeFor(MethodDeclarationSyntax declaration, HeaderChange header)
    {
        if (header.IsEmpty)
        {
            return null;
        }

        var access = header.Accessibility is { } a ? a.Split(' ') : System.Array.Empty<string>();
        var kept = header.Accessibility is null
            ? declaration.Modifiers.Select(m => m.ValueText)
            : declaration.Modifiers.Select(m => m.ValueText).Where(v => !AccessWords.Contains(v));
        var returnType = header.ReturnType ?? declaration.ReturnType.ToString();
        var text = string.Join(" ", access.Concat(kept).Append(returnType));
        var start = declaration.Modifiers.Count > 0 ? declaration.Modifiers[0].SpanStart : declaration.ReturnType.SpanStart;
        return new TextChange(TextSpan.FromBounds(start, declaration.ReturnType.Span.End), text);
    }

    /// <summary>The declaration's text with its parameter list and header rewritten. Used when the method is planned alone.</summary>
    public static string NewText(MethodDeclarationSyntax declaration, string parameterList, HeaderChange header)
    {
        var changes = new List<TextChange> { new TextChange(declaration.ParameterList.Span, parameterList) };
        if (TextChangeFor(declaration, header) is { } headerChange)
        {
            changes.Add(headerChange);
        }

        var text = declaration.ToString();
        var origin = declaration.SpanStart;
        foreach (var change in changes.OrderByDescending(c => c.Span.Start))
        {
            text = text.Substring(0, change.Span.Start - origin) + change.NewText + text.Substring(change.Span.End - origin);
        }

        return text;
    }
}
