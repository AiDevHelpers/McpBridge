using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>What a position in a file names: the document it is in, the declaration that holds it and that declaration's symbol.</summary>
internal sealed class EditTarget
{
    public EditTarget(DocumentId documentId, SyntaxNode declaration, ISymbol symbol)
    {
        DocumentId = documentId;
        Declaration = declaration;
        Symbol = symbol;
    }

    public DocumentId DocumentId { get; }

    public SyntaxNode Declaration { get; }

    public ISymbol Symbol { get; }

    /// <summary>Every member declaration kind the edit tools can name. Each engine passes the subset it supports.</summary>
    public static readonly Type[] AllMemberKinds =
    [
        typeof(MethodDeclarationSyntax),
        typeof(ConstructorDeclarationSyntax),
        typeof(DestructorDeclarationSyntax),
        typeof(OperatorDeclarationSyntax),
        typeof(ConversionOperatorDeclarationSyntax),
        typeof(IndexerDeclarationSyntax),
        typeof(PropertyDeclarationSyntax),
        typeof(FieldDeclarationSyntax),
        typeof(EventDeclarationSyntax),
        typeof(EventFieldDeclarationSyntax),
        typeof(EnumMemberDeclarationSyntax),
    ];

    /// <summary>
    /// The innermost declaration of one of <paramref name="kinds"/> that contains the position of <paramref name="line"/> (and column,
    /// when given). Null with <paramref name="reason"/> set when there is none, the file is unknown, or the line is out of range.
    /// A field that declares several variables is named, not guessed: the caller is told to split it.
    /// </summary>
    public static async Task<EditTarget?> LocateAsync(Solution solution, string file, int line, int column, IReadOnlyCollection<Type> kinds, Action<string> reason)
    {
        var id = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault();
        var document = id is null ? null : solution.GetDocument(id);
        var text = document is null ? null : await document.GetTextAsync().ConfigureAwait(false);
        if (document is null || text is null || line < 1 || line > text.Lines.Count)
        {
            reason(Strings.Format("NoRoslynDocumentFor", file));
            return null;
        }

        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        var lineText = text.Lines[line - 1].ToString();
        var offset = Math.Max(0, column < 1 ? lineText.Length - lineText.TrimStart().Length : column - 1);
        var position = Math.Min(text.Lines[line - 1].Start + offset, text.Length);
        var node = root!.FindToken(Math.Min(position, root.FullSpan.End)).Parent?.AncestorsAndSelf()
            .FirstOrDefault(n => kinds.Any(k => k.IsInstanceOfType(n)));
        if (node is null)
        {
            reason(Strings.Get("EditTargetNone"));
            return null;
        }

        if (node is FieldDeclarationSyntax { Declaration.Variables.Count: > 1 } || node is EventFieldDeclarationSyntax { Declaration.Variables.Count: > 1 })
        {
            reason(Strings.Get("EditTargetMultiDeclarator"));
            return null;
        }

        var symbol = SymbolOf(model!, node);
        if (symbol is null)
        {
            reason(Strings.Get("EditTargetNoSymbol"));
            return null;
        }

        return new EditTarget(id!, node, symbol);
    }

    /// <summary>The symbol a declaration node declares, for each member kind above. Null for anything else.</summary>
    public static ISymbol? SymbolOf(SemanticModel model, SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => model.GetDeclaredSymbol(m),
        ConstructorDeclarationSyntax c => model.GetDeclaredSymbol(c),
        DestructorDeclarationSyntax d => model.GetDeclaredSymbol(d),
        OperatorDeclarationSyntax o => model.GetDeclaredSymbol(o),
        ConversionOperatorDeclarationSyntax co => model.GetDeclaredSymbol(co),
        IndexerDeclarationSyntax i => model.GetDeclaredSymbol(i),
        PropertyDeclarationSyntax p => model.GetDeclaredSymbol(p),
        FieldDeclarationSyntax f => model.GetDeclaredSymbol(f.Declaration.Variables[0]),
        EventDeclarationSyntax e => model.GetDeclaredSymbol(e),
        EventFieldDeclarationSyntax ef => model.GetDeclaredSymbol(ef.Declaration.Variables[0]),
        EnumMemberDeclarationSyntax em => model.GetDeclaredSymbol(em),
        _ => null,
    };
}
