using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>One piece of documentation text the caller supplies: tag is <c>param</c>, <c>typeparam</c> or <c>returns</c>.</summary>
internal sealed class DocItem
{
    public DocItem(string tag, string? name, string text)
    {
        Tag = tag;
        Name = name;
        Text = text;
    }

    public string Tag { get; }

    public string? Name { get; }

    public string Text { get; }
}

/// <summary>What the caller wants written into the XML doc comment: a new summary and text for new or changed tags.</summary>
internal sealed class DocUpdates
{
    public string? NewSummary { get; set; }

    public List<DocItem> Items { get; } = new();
}

internal sealed class DocUpdateResult
{
    public TextChange? Change { get; set; }

    public List<string> Missing { get; } = new();

    public List<string> Warnings { get; } = new();

    /// <summary>The rebuilt doc comment, one entry per line, each starting with the prefix and without indentation.</summary>
    public List<string> Lines { get; } = new();
}

/// <summary>
/// Rewrites the <c>///</c> doc comment of an edited method so its param, typeparam and returns tags match the new signature,
/// in the layout the comment already has. It never invents text: new tags need text from the caller. A method with no doc
/// comment is left alone.
/// </summary>
internal static class MethodDocUpdater
{
    private sealed class Block
    {
        public string Kind { get; set; } = "";

        public string? Name { get; set; }

        public string Raw { get; set; } = "";

        public string Inner { get; set; } = "";

        public bool Multiline { get; set; }
    }

    public static DocUpdateResult Update(
        BaseMethodDeclarationSyntax oldNode, IMethodSymbol oldSymbol, IMethodSymbol newSymbol, DocUpdates updates, bool signatureChanged, string eol)
    {
        var result = new DocUpdateResult();
        var leading = oldNode.GetLeadingTrivia();
        var trivia = leading.FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia));
        if (trivia == default)
        {
            if (leading.Any(t => t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)))
            {
                result.Warnings.Add(Strings.Get("MethodEditDocStyleUnsupported"));
            }

            return result;
        }

        var doc = (DocumentationCommentTriviaSyntax)trivia.GetStructure()!;
        var first = trivia.ToFullString();
        var prefix = first.Length > 3 && first[3] == ' ' ? "/// " : "///";
        var index = leading.IndexOf(trivia);
        var indent = index > 0 && leading[index - 1].IsKind(SyntaxKind.WhitespaceTrivia) ? leading[index - 1].ToString() : "";

        var blocks = ParseBlocks(doc);
        var rebuilt = Rebuild(blocks, oldSymbol, newSymbol, updates, signatureChanged, result);
        if (result.Missing.Count > 0)
        {
            return result;
        }

        var lines = new List<string>();
        foreach (var block in rebuilt)
        {
            Render(block, prefix, lines);
        }

        result.Lines.AddRange(lines);
        var text = string.Join(eol, lines.Select((l, i) => i == 0 ? l : indent + l)) + eol;
        if (text != trivia.ToFullString())
        {
            result.Change = new TextChange(trivia.FullSpan, text);
        }

        return result;
    }

    /// <summary>
    /// Replaces the summary of a member's existing <c>///</c> doc comment, keeping its layout and every other tag. Null when the
    /// member has no such comment.
    /// </summary>
    public static TextChange? ReplaceSummary(SyntaxNode member, string summary, string eol)
    {
        var leading = member.GetLeadingTrivia();
        var trivia = leading.FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia));
        if (trivia == default)
        {
            return null;
        }

        var first = trivia.ToFullString();
        var prefix = first.Length > 3 && first[3] == ' ' ? "/// " : "///";
        var index = leading.IndexOf(trivia);
        var indent = index > 0 && leading[index - 1].IsKind(SyntaxKind.WhitespaceTrivia) ? leading[index - 1].ToString() : "";
        var blocks = ParseBlocks((DocumentationCommentTriviaSyntax)trivia.GetStructure()!);
        var lines = new List<string>();
        foreach (var block in blocks)
        {
            Render(block.Kind == "summary" ? WithText(block, summary, null, block.Multiline || summary.Contains('\n')) : block, prefix, lines);
        }

        var text = string.Join(eol, lines.Select((l, i) => i == 0 ? l : indent + l)) + eol;
        return text == trivia.ToFullString() ? null : new TextChange(trivia.FullSpan, text);
    }

    /// <summary>
    /// Doc lines for a method that has none yet, in the layout of a documented neighbour (<paramref name="donor"/>, may be null).
    /// Summary, every parameter, every type parameter and a non-void return need text from the caller; nothing is invented.
    /// </summary>
    public static DocUpdateResult BuildNew(MethodDeclarationSyntax? donor, IMethodSymbol symbol, DocUpdates updates)
    {
        var result = new DocUpdateResult();
        var prefix = "/// ";
        var multilineSummary = false;
        var multilineParams = false;
        var trivia = donor?.GetLeadingTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia));
        if (trivia is { } found && found != default)
        {
            var text = found.ToFullString();
            prefix = text.Length > 3 && text[3] == ' ' ? "/// " : "///";
            var blocks = ParseBlocks((DocumentationCommentTriviaSyntax)found.GetStructure()!);
            multilineSummary = blocks.FirstOrDefault(b => b.Kind == "summary")?.Multiline == true;
            multilineParams = blocks.FirstOrDefault(b => b.Kind == "param")?.Multiline == true;
        }

        var built = new List<Block>();
        if (updates.NewSummary is null)
        {
            result.Missing.Add("summary");
        }
        else
        {
            built.Add(WithText(new Block { Kind = "summary" }, updates.NewSummary, null, multilineSummary));
        }

        foreach (var tp in symbol.TypeParameters)
        {
            AddNew(built, result, updates, "typeparam", tp.Name, multilineParams);
        }

        foreach (var p in symbol.Parameters)
        {
            AddNew(built, result, updates, "param", p.Name, multilineParams);
        }

        if (!symbol.ReturnsVoid)
        {
            AddNew(built, result, updates, "returns", null, multilineParams);
        }

        if (result.Missing.Count == 0)
        {
            foreach (var block in built)
            {
                Render(block, prefix, result.Lines);
            }
        }

        return result;
    }

    private static void AddNew(List<Block> built, DocUpdateResult result, DocUpdates updates, string tag, string? name, bool multiline)
    {
        var given = updates.Items.FirstOrDefault(d => d.Tag == tag && d.Name == name);
        if (given is null)
        {
            result.Missing.Add(name is null ? tag : tag + " " + name);
            return;
        }

        built.Add(WithText(new Block { Kind = tag, Name = name }, given.Text, name, multiline));
    }

    private static List<Block> ParseBlocks(DocumentationCommentTriviaSyntax doc)
    {
        var blocks = new List<Block>();
        foreach (var node in doc.Content)
        {
            if (node is XmlElementSyntax element)
            {
                var inner = StripPrefixes(element.Content.ToFullString());
                blocks.Add(new Block
                {
                    Kind = element.StartTag.Name.ToString(),
                    Name = NameAttribute(element.StartTag),
                    Raw = StripPrefixes(element.ToFullString()).TrimEnd('\r', '\n'),
                    Inner = inner,
                    Multiline = inner.Contains('\n'),
                });
            }
            else if (node is XmlEmptyElementSyntax empty)
            {
                blocks.Add(new Block { Kind = empty.Name.ToString(), Name = NameAttribute(empty), Raw = StripPrefixes(empty.ToFullString()).TrimEnd('\r', '\n') });
            }
        }

        return blocks;
    }

    private static string? NameAttribute(SyntaxNode tag) =>
        tag.DescendantNodes().OfType<XmlNameAttributeSyntax>().FirstOrDefault()?.Identifier.Identifier.ValueText;

    // Removes the leading "///" (and one space) that continuation lines carry inside a doc comment.
    private static string StripPrefixes(string text) => Regex.Replace(text, @"(\r?\n)[ \t]*///[ \t]?", "$1");

    private static List<Block> Rebuild(
        List<Block> old, IMethodSymbol oldSymbol, IMethodSymbol newSymbol, DocUpdates updates, bool signatureChanged, DocUpdateResult result)
    {
        var rebuilt = new List<Block>();
        var renames = new Dictionary<string, string>();
        var paramsDone = false;
        var typeParamsDone = false;
        var oldParamBlocks = old.Where(b => b.Kind == "param").ToList();
        var oldTypeBlocks = old.Where(b => b.Kind == "typeparam").ToList();
        var multilineParams = oldParamBlocks.Count > 0 && oldParamBlocks[0].Multiline;

        // Which new parameter is which old one: by name, then by position and type when only the name changed.
        var matched = new Dictionary<int, IParameterSymbol>();
        for (var i = 0; i < newSymbol.Parameters.Length; i++)
        {
            var p = newSymbol.Parameters[i];
            var same = oldSymbol.Parameters.FirstOrDefault(o => o.Name == p.Name);
            if (same is null && i < oldSymbol.Parameters.Length)
            {
                var o = oldSymbol.Parameters[i];
                if (newSymbol.Parameters.All(n => n.Name != o.Name) && o.Type.ToDisplayString() == p.Type.ToDisplayString())
                {
                    same = o;
                }
            }

            if (same is not null)
            {
                matched[i] = same;
            }
        }

        foreach (var block in old)
        {
            switch (block.Kind)
            {
                case "summary":
                    rebuilt.Add(updates.NewSummary is null ? block : WithText(block, updates.NewSummary, null, block.Multiline));
                    if (updates.NewSummary is null && signatureChanged)
                    {
                        result.Warnings.Add(Strings.Get("MethodEditSummaryMayBeStale"));
                    }

                    break;
                case "param" when !paramsDone:
                    paramsDone = true;
                    for (var i = 0; i < newSymbol.Parameters.Length; i++)
                    {
                        var p = newSymbol.Parameters[i];
                        var given = updates.Items.FirstOrDefault(d => d.Tag == "param" && d.Name == p.Name);
                        var carried = matched.TryGetValue(i, out var o) ? oldParamBlocks.FirstOrDefault(b => b.Name == o.Name) : null;
                        if (given is not null)
                        {
                            rebuilt.Add(WithText(new Block { Kind = "param", Name = p.Name }, given.Text, p.Name, multilineParams || given.Text.Contains('\n')));
                        }
                        else if (carried is not null)
                        {
                            rebuilt.Add(Rename(carried, p.Name));
                            if (carried.Name is { } oldName && oldName != p.Name)
                            {
                                renames[oldName] = p.Name;
                            }

                            if (matched[i].Type.ToDisplayString() != p.Type.ToDisplayString())
                            {
                                result.Warnings.Add(Strings.Format("MethodEditParamTypeChanged", p.Name));
                            }
                        }
                        else
                        {
                            result.Missing.Add("param " + p.Name);
                        }
                    }

                    break;
                case "param":
                    break;
                case "typeparam" when !typeParamsDone:
                    typeParamsDone = true;
                    foreach (var tp in newSymbol.TypeParameters)
                    {
                        var given = updates.Items.FirstOrDefault(d => d.Tag == "typeparam" && d.Name == tp.Name);
                        var carried = oldTypeBlocks.FirstOrDefault(b => b.Name == tp.Name);
                        if (given is not null)
                        {
                            rebuilt.Add(WithText(new Block { Kind = "typeparam", Name = tp.Name }, given.Text, tp.Name, given.Text.Contains('\n')));
                        }
                        else if (carried is not null)
                        {
                            rebuilt.Add(carried);
                        }
                        else
                        {
                            result.Missing.Add("typeparam " + tp.Name);
                        }
                    }

                    break;
                case "typeparam":
                    break;
                case "returns":
                    var returns = updates.Items.FirstOrDefault(d => d.Tag == "returns");
                    if (newSymbol.ReturnsVoid)
                    {
                        break;
                    }

                    rebuilt.Add(returns is null ? block : WithText(block, returns.Text, null, block.Multiline || returns.Text.Contains('\n')));
                    if (returns is null && oldSymbol.ReturnType.ToDisplayString() != newSymbol.ReturnType.ToDisplayString())
                    {
                        result.Warnings.Add(Strings.Get("MethodEditReturnsMayBeStale"));
                    }

                    break;
                default:
                    rebuilt.Add(block);
                    break;
            }
        }

        // A returns tag the method did not have, when the caller gives one.
        if (!old.Any(b => b.Kind == "returns") && !newSymbol.ReturnsVoid && updates.Items.FirstOrDefault(d => d.Tag == "returns") is { } added)
        {
            rebuilt.Add(WithText(new Block { Kind = "returns" }, added.Text, null, added.Text.Contains('\n')));
        }

        // A renamed parameter is also renamed where other tags point at it with paramref.
        foreach (var block in rebuilt.Where(b => b.Inner.Length > 0))
        {
            foreach (var rename in renames)
            {
                block.Inner = Regex.Replace(block.Inner, "(<paramref\\s+name=\")" + Regex.Escape(rename.Key) + "\"", "${1}" + rename.Value + "\"");
            }
        }

        return rebuilt;
    }

    private static Block Rename(Block block, string name) => new()
    {
        Kind = block.Kind,
        Name = name,
        Raw = block.Raw,
        Inner = block.Inner,
        Multiline = block.Multiline,
    };

    private static Block WithText(Block block, string text, string? name, bool multiline) => new()
    {
        Kind = block.Kind,
        Name = name ?? block.Name,
        Inner = text,
        Multiline = multiline || text.Contains('\n'),
    };

    private static void Render(Block block, string prefix, List<string> lines)
    {
        var attribute = block.Name is null ? "" : " name=\"" + block.Name + "\"";
        if (block.Raw.Length > 0 && block.Inner.Length == 0)
        {
            lines.AddRange(block.Raw.Split('\n').Select(l => prefix + l.TrimEnd('\r')));
            return;
        }

        var open = "<" + block.Kind + attribute + ">";
        var close = "</" + block.Kind + ">";
        var inner = block.Inner.Replace("\r\n", "\n");
        if (!block.Multiline)
        {
            lines.Add(prefix + open + inner.Trim() + close);
            return;
        }

        lines.Add(prefix + open);
        foreach (var line in inner.Trim('\n').Split('\n'))
        {
            lines.Add(line.Length == 0 ? prefix.TrimEnd() : prefix + line.TrimEnd('\r'));
        }

        lines.Add(prefix + close);
    }
}
