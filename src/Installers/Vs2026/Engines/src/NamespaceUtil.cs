using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>Shared pieces of the namespace tools: names, written paths, usings, string mentions and the folder convention.</summary>
internal static class NamespaceUtil
{
    /// <summary>A dotted name of plain identifiers, none of them a C# keyword.</summary>
    public static bool IsValidQualified(string name) =>
        name.Length > 0 && name.Split('.').All(s => SyntaxFacts.IsValidIdentifier(s) && SyntaxFacts.GetKeywordKind(s) == SyntaxKind.None);

    /// <summary>
    /// The written namespace path that ends at <paramref name="segment"/>: its span from the leftmost name (after any
    /// <c>global::</c>) to the segment, and its text without whitespace.
    /// </summary>
    public static (TextSpan Span, string Written) ChainOf(SyntaxToken segment)
    {
        SyntaxNode node = segment.Parent!;
        while (true)
        {
            var parent = node.Parent;
            if (parent is QualifiedNameSyntax q && q.Right == node)
            {
                node = q;
            }
            else if (parent is MemberAccessExpressionSyntax m && m.Name == node)
            {
                node = m;
            }
            else if (parent is AliasQualifiedNameSyntax a && a.Name == node)
            {
                node = a;
                break;
            }
            else
            {
                break;
            }
        }

        var left = node;
        while (true)
        {
            if (left is QualifiedNameSyntax lq)
            {
                left = lq.Left;
            }
            else if (left is MemberAccessExpressionSyntax lm)
            {
                left = lm.Expression;
            }
            else if (left is AliasQualifiedNameSyntax la)
            {
                left = la.Name;
                break;
            }
            else
            {
                break;
            }
        }

        var span = TextSpan.FromBounds(left.SpanStart, segment.Span.End);
        return (span, Regex.Replace(node.SyntaxTree.GetText().ToString(span), @"\s+", ""));
    }

    /// <summary>The full name of a namespace declaration, counting the block namespaces it sits in.</summary>
    public static string FullName(BaseNamespaceDeclarationSyntax declaration) =>
        string.Join(".", declaration.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>().Reverse()
            .Select(n => Regex.Replace(n.Name.ToString(), @"\s+", "")));

    /// <summary>String literals in the document that mention the text. Roslyn cannot update these; the caller is told.</summary>
    public static IEnumerable<object> StringMentions(Document document, SyntaxNode root, string text)
    {
        foreach (var token in root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken) || t.IsKind(SyntaxKind.InterpolatedStringTextToken)))
        {
            if (token.ValueText.IndexOf(text, StringComparison.Ordinal) >= 0)
            {
                var snippet = token.ValueText.Length > 80 ? token.ValueText.Substring(0, 80) : token.ValueText;
                yield return new { file = document.FilePath, line = token.GetLocation().GetLineSpan().StartLinePosition.Line + 1, text = snippet };
            }
        }
    }

    // ---- usings ----

    /// <summary>The text change that adds <c>using name;</c> to the document, in sorted place among its usings. Null when it is there.</summary>
    public static TextChange? AddUsing(SyntaxNode root, SourceText text, string name, string eol)
    {
        var unit = (CompilationUnitSyntax)root;
        var plain = unit.Usings.Where(u => u.Alias is null && u.StaticKeyword == default).ToList();
        if (unit.Usings.Any(u => u.Alias is null && Regex.Replace(u.Name!.ToString(), @"\s+", "") == name))
        {
            return null;
        }

        var line = "using " + name + ";";
        if (plain.Count > 0)
        {
            var after = plain.LastOrDefault(u => string.CompareOrdinal(u.Name!.ToString(), name) < 0);
            if (after is not null)
            {
                return new TextChange(new TextSpan(text.Lines.GetLineFromPosition(after.Span.End).End, 0), eol + line);
            }

            var first = plain[0];
            return new TextChange(new TextSpan(text.Lines.GetLineFromPosition(first.SpanStart).Start, 0), line + eol);
        }

        if (unit.Usings.Count > 0)
        {
            return new TextChange(new TextSpan(text.Lines.GetLineFromPosition(unit.Usings[0].SpanStart).Start, 0), line + eol);
        }

        var firstMember = unit.Members.FirstOrDefault();
        var at = firstMember is null ? 0 : text.Lines.GetLineFromPosition(firstMember.SpanStart).Start;
        return new TextChange(new TextSpan(at, 0), line + eol + eol);
    }

    /// <summary>The text change that removes the whole line of a plain <c>using name;</c>. Null when there is none.</summary>
    public static TextChange? RemoveUsing(SyntaxNode root, SourceText text, string name)
    {
        var unit = (CompilationUnitSyntax)root;
        var directive = unit.Usings.FirstOrDefault(u => u.Alias is null && u.StaticKeyword == default && Regex.Replace(u.Name!.ToString(), @"\s+", "") == name);
        if (directive is null)
        {
            return null;
        }

        var line = text.Lines.GetLineFromPosition(directive.SpanStart);
        return new TextChange(TextSpan.FromBounds(line.Start, line.EndIncludingLineBreak), "");
    }

    // ---- folder convention ----

    /// <summary>The namespace the folder of a file implies: the project's root namespace plus the folders below the project. Null when unknown.</summary>
    public static string? ExpectedNamespace(string? projectDirectory, string? rootNamespace, string? filePath)
    {
        if (string.IsNullOrEmpty(projectDirectory) || string.IsNullOrEmpty(rootNamespace) || string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(filePath)!;
        var root = projectDirectory!.TrimEnd('\\', '/');
        if (!directory.Equals(root, StringComparison.OrdinalIgnoreCase) && !directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = directory.Substring(root.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Select(Sanitize);
        return string.Join(".", new[] { rootNamespace! }.Concat(segments));
    }

    private static string Sanitize(string folder)
    {
        var clean = Regex.Replace(folder, @"[^\w]", "_");
        return char.IsDigit(clean[0]) ? "_" + clean : clean;
    }

    /// <summary>The folder a namespace implies below the project, or null when the namespace is outside the root namespace.</summary>
    public static string? FolderFor(string? projectDirectory, string? rootNamespace, string newNamespace)
    {
        if (string.IsNullOrEmpty(projectDirectory) || string.IsNullOrEmpty(rootNamespace))
        {
            return null;
        }

        if (newNamespace == rootNamespace)
        {
            return projectDirectory;
        }

        return newNamespace.StartsWith(rootNamespace + ".", StringComparison.Ordinal)
            ? Path.Combine(new[] { projectDirectory! }.Concat(newNamespace.Substring(rootNamespace!.Length + 1).Split('.')).ToArray())
            : null;
    }

    /// <summary>True when most documents of the project sit in the namespace their folder implies.</summary>
    public static async System.Threading.Tasks.Task<bool> FollowsConventionAsync(Project project)
    {
        var directory = project.FilePath is null ? null : Path.GetDirectoryName(project.FilePath);
        var total = 0;
        var matching = 0;
        foreach (var document in project.Documents)
        {
            var expected = ExpectedNamespace(directory, project.DefaultNamespace, document.FilePath);
            var root = expected is null ? null : await document.GetSyntaxRootAsync().ConfigureAwait(false);
            var declaration = root?.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
            if (declaration is null)
            {
                continue;
            }

            total++;
            if (FullName(declaration) == expected)
            {
                matching++;
            }
        }

        return total > 0 && matching * 5 >= total * 4;
    }
}
