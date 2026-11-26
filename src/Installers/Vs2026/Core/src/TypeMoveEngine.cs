using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Plans moving one top-level type to another namespace: its declaration, every qualified use, and the usings that make the new
/// namespace visible to code that used the type by its simple name. Pure: it changes nothing; the caller applies
/// <see cref="MethodEditPlan.Result"/>.
/// </summary>
internal static class TypeMoveEngine
{
    public static async Task<MethodEditPlan> MoveAsync(
        Solution solution, string file, int line, string newNamespace, bool moveFiles, IReadOnlyList<CallerEdit> callerEdits)
    {
        var plan = new MethodEditPlan { NewSignature = newNamespace };
        if (!NamespaceUtil.IsValidQualified(newNamespace))
        {
            plan.Reasons.Add(Strings.Format("NamespaceBadName", newNamespace));
            return plan;
        }

        var documentId = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault();
        var document = documentId is null ? null : solution.GetDocument(documentId);
        var text = document is null ? null : await document.GetTextAsync().ConfigureAwait(false);
        if (document is null || text is null || line < 1 || line > text.Lines.Count)
        {
            plan.Reasons.Add(Strings.Format("NoRoslynDocumentFor", file));
            return plan;
        }

        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        var lineText = text.Lines[line - 1].ToString();
        var position = text.Lines[line - 1].Start + (lineText.Length - lineText.TrimStart().Length);
        var node = root!.FindToken(Math.Min(position, root.FullSpan.End)).Parent?.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        var symbol = node is null ? null : model!.GetDeclaredSymbol(node);
        if (node is null || symbol is null)
        {
            plan.Reasons.Add(Strings.Get("TypeMoveNotAType"));
            return plan;
        }

        if (symbol.ContainingType is not null)
        {
            plan.Reasons.Add(Strings.Get("TypeMoveNested"));
            return plan;
        }

        var oldNamespace = symbol.ContainingNamespace.ToDisplayString();
        plan.OldSignature = oldNamespace + "." + symbol.Name;
        if (symbol.ContainingNamespace.IsGlobalNamespace)
        {
            plan.Reasons.Add(Strings.Get("TypeMoveGlobal"));
            return plan;
        }

        if (oldNamespace == newNamespace)
        {
            plan.Reasons.Add(Strings.Get("NamespaceSame"));
            return plan;
        }

        var compilation = await document.Project.GetCompilationAsync().ConfigureAwait(false);
        if (compilation!.GetTypeByMetadataName(newNamespace + "." + symbol.MetadataName) is not null)
        {
            plan.Reasons.Add(Strings.Format("NamespaceCollision", symbol.Name, newNamespace));
            return plan;
        }

        // 1. Every declaring file (a partial type has several) must hold only this type, so its namespace name can change.
        var changes = new Dictionary<DocumentId, List<TextChange>>();
        var declaringDocuments = new HashSet<DocumentId>();
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var declaringDocument = solution.GetDocument(reference.SyntaxTree)!;
            var declaration = (await reference.GetSyntaxAsync().ConfigureAwait(false)).Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
            if (declaration is null || declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Any())
            {
                plan.Reasons.Add(Strings.Format("NamespaceNestedBlock", declaringDocument.FilePath, (await reference.GetSyntaxAsync()).GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                continue;
            }

            var others = declaration.Members.OfType<BaseTypeDeclarationSyntax>().Where(m => m.Identifier.ValueText != symbol.Name).Select(m => m.Identifier.ValueText).ToList();
            if (others.Count > 0 || declaration.Members.Any(m => m is not BaseTypeDeclarationSyntax))
            {
                plan.Reasons.Add(Strings.Format("TypeMoveSharedFile", declaringDocument.FilePath, string.Join(", ", others)));
                continue;
            }

            NamespaceEngine.Add(changes, declaringDocument.Id, new TextChange(declaration.Name.Span, newNamespace));
            declaringDocuments.Add(declaringDocument.Id);
        }

        if (plan.Refused)
        {
            return plan;
        }

        plan.DeclarationCount = changes.Values.Sum(l => l.Count);

        // 2. Uses of the type: qualified ones are rewritten; simple-name uses need the new namespace visible.
        var needsUsing = new Dictionary<DocumentId, SortedSet<string>>();
        var references = (await SymbolFinder.FindReferencesAsync(symbol, solution).ConfigureAwait(false)).SelectMany(r => r.Locations).ToList();
        plan.ReferenceCount = references.Count;
        foreach (var location in references)
        {
            var id = location.Document.Id;
            var referenceRoot = await location.Document.GetSyntaxRootAsync().ConfigureAwait(false);
            var token = referenceRoot!.FindToken(location.Location.SourceSpan.Start);
            var qualifier = QualifierOf(token);
            var span = location.Location.GetLineSpan();
            if (qualifier is { } q)
            {
                var caller = callerEdits.FirstOrDefault(e => string.Equals(e.File, span.Path, StringComparison.OrdinalIgnoreCase) && e.Line == span.StartLinePosition.Line + 1 && e.Column == span.StartLinePosition.Character + 1);
                if (caller is not null)
                {
                    NamespaceEngine.Add(changes, id, new TextChange(q.Span, caller.NewText));
                }
                else if (q.Written == oldNamespace)
                {
                    NamespaceEngine.Add(changes, id, new TextChange(q.Span, newNamespace));
                }
                else
                {
                    plan.Uncovered.Add(new { file = span.Path, line = span.StartLinePosition.Line + 1, column = span.StartLinePosition.Character + 1, written = q.Written, namespace_name = oldNamespace });
                }

                continue;
            }

            if (declaringDocuments.Contains(id))
            {
                continue;
            }

            var enclosing = token.Parent!.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
            var enclosingName = enclosing is null ? "" : NamespaceUtil.FullName(enclosing);
            if (enclosingName == newNamespace || enclosingName.StartsWith(newNamespace + ".", StringComparison.Ordinal))
            {
                continue;
            }

            Need(needsUsing, id, newNamespace);
        }

        if (plan.Uncovered.Count > 0)
        {
            plan.Reasons.Add(Strings.Format("NamespaceRelativeReferences", plan.Uncovered.Count));
            return plan;
        }

        // 3. The moved type used its old namespace and its parents without saying so. It needs them spelled out now.
        foreach (var id in declaringDocuments)
        {
            var declaringDocument = solution.GetDocument(id)!;
            foreach (var name in await ImplicitNamespacesAsync(declaringDocument, symbol, newNamespace).ConfigureAwait(false))
            {
                Need(needsUsing, id, name);
            }
        }

        // 4. Usings of the old namespace that nothing in the file needs any more.
        var removals = new Dictionary<DocumentId, TextChange>();
        foreach (var project in solution.Projects)
        {
            foreach (var other in project.Documents.Where(d => !declaringDocuments.Contains(d.Id)))
            {
                var otherRoot = await other.GetSyntaxRootAsync().ConfigureAwait(false);
                if (otherRoot is not CompilationUnitSyntax unit || !unit.Usings.Any(u => u.Alias is null && Regex.Replace(u.Name!.ToString(), @"\s+", "") == oldNamespace))
                {
                    continue;
                }

                if (!await StillUsesAsync(other, oldNamespace, symbol).ConfigureAwait(false) && NamespaceUtil.RemoveUsing(otherRoot, await other.GetTextAsync().ConfigureAwait(false), oldNamespace) is { } removal)
                {
                    removals[other.Id] = removal;
                }
            }
        }

        // 5. Build the text changes: usings to add (one block per file) and to remove.
        foreach (var pair in needsUsing)
        {
            var target = solution.GetDocument(pair.Key)!;
            var targetText = await target.GetTextAsync().ConfigureAwait(false);
            var targetRoot = await target.GetSyntaxRootAsync().ConfigureAwait(false);
            var eol = targetText.ToString().Contains("\r\n") ? "\r\n" : "\n";
            if (UsingBlock(targetRoot!, targetText, pair.Value, eol) is { } add)
            {
                NamespaceEngine.Add(changes, pair.Key, add);
            }
        }

        foreach (var pair in removals)
        {
            NamespaceEngine.Add(changes, pair.Key, pair.Value);
        }

        // 6. Strings, folder convention, then apply and check.
        foreach (var other in solution.Projects.SelectMany(p => p.Documents))
        {
            var otherRoot = await other.GetSyntaxRootAsync().ConfigureAwait(false);
            if (otherRoot is not null && plan.StringMentions.Count < 50)
            {
                plan.StringMentions.AddRange(NamespaceUtil.StringMentions(other, otherRoot, oldNamespace + "." + symbol.Name).Take(50 - plan.StringMentions.Count));
            }
        }

        if (plan.StringMentions.Count > 0)
        {
            plan.Warnings.Add(Strings.Format("NamespaceStringMentions", plan.StringMentions.Count));
        }

        foreach (var group in declaringDocuments.Select(id => solution.GetDocument(id)!).GroupBy(d => d.Project))
        {
            if (group.Key.FilePath is null || !await NamespaceUtil.FollowsConventionAsync(group.Key).ConfigureAwait(false))
            {
                continue;
            }

            var folder = NamespaceUtil.FolderFor(System.IO.Path.GetDirectoryName(group.Key.FilePath), group.Key.DefaultNamespace, newNamespace);
            foreach (var d in group.Where(d => folder is not null && d.FilePath is not null && !string.Equals(System.IO.Path.GetDirectoryName(d.FilePath), folder, StringComparison.OrdinalIgnoreCase)))
            {
                if (moveFiles)
                {
                    plan.FileMoves.Add(new FileMove(d.FilePath!, System.IO.Path.Combine(folder!, System.IO.Path.GetFileName(d.FilePath!)), group.Key.FilePath));
                }
                else
                {
                    plan.Warnings.Add(Strings.Format("NamespaceFolderMismatch", 1));
                }
            }
        }

        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        foreach (var id in changes.Keys)
        {
            plan.Files.Add(solution.GetDocument(id)!.FilePath ?? id.ToString());
        }

        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, changes.Keys, plan).ConfigureAwait(false);
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    private static void Need(Dictionary<DocumentId, SortedSet<string>> needs, DocumentId id, string name)
    {
        if (!needs.TryGetValue(id, out var set))
        {
            needs[id] = set = new SortedSet<string>(StringComparer.Ordinal);
        }

        set.Add(name);
    }

    // The namespace path written in front of a type name, when the name is qualified (a.b.T); null for a simple name.
    private static (TextSpan Span, string Written)? QualifierOf(SyntaxToken token)
    {
        var name = token.Parent as IdentifierNameSyntax;
        SyntaxNode? left = name?.Parent switch
        {
            QualifiedNameSyntax q when q.Right == name => q.Left,
            MemberAccessExpressionSyntax m when m.Name == name => m.Expression,
            AliasQualifiedNameSyntax => null,
            _ => null,
        };
        if (left is null)
        {
            return null;
        }

        var written = Regex.Replace(left.ToString(), @"\s+", "");
        var leftmost = left;
        while (true)
        {
            if (leftmost is QualifiedNameSyntax lq)
            {
                leftmost = lq.Left;
            }
            else if (leftmost is MemberAccessExpressionSyntax lm)
            {
                leftmost = lm.Expression;
            }
            else if (leftmost is AliasQualifiedNameSyntax la)
            {
                leftmost = la.Name;
                written = Regex.Replace(left.ToString(), @"\s+", "");
                written = written.StartsWith("global::", StringComparison.Ordinal) ? written.Substring(8) : written;
                break;
            }
            else
            {
                break;
            }
        }

        return (TextSpan.FromBounds(leftmost.SpanStart, left.Span.End), written);
    }

    // Namespaces the moved type's file uses by simple name without importing them, and that the new namespace does not see.
    private static async Task<IEnumerable<string>> ImplicitNamespacesAsync(Document document, INamedTypeSymbol moved, string newNamespace)
    {
        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        var imported = new HashSet<string>(((CompilationUnitSyntax)root!).Usings.Where(u => u.Alias is null).Select(u => Regex.Replace(u.Name!.ToString(), @"\s+", "")));
        var needed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var id in root!.DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => !i.Ancestors().OfType<UsingDirectiveSyntax>().Any() && !IsQualified(i)))
        {
            var symbol = model!.GetSymbolInfo(id).Symbol;
            var container = symbol switch
            {
                INamedTypeSymbol t when !SymbolEqualityComparer.Default.Equals(t, moved) && t.ContainingType is null => t.ContainingNamespace,
                IMethodSymbol { IsExtensionMethod: true } m => m.ContainingNamespace,
                _ => null,
            };
            if (container is null || container.IsGlobalNamespace)
            {
                continue;
            }

            var name = container.ToDisplayString();
            if (name != newNamespace && !newNamespace.StartsWith(name + ".", StringComparison.Ordinal) && !imported.Contains(name))
            {
                needed.Add(name);
            }
        }

        return needed;
    }

    private static bool IsQualified(IdentifierNameSyntax id) =>
        (id.Parent is QualifiedNameSyntax q && q.Right == id) || (id.Parent is MemberAccessExpressionSyntax m && m.Name == id);

    // Whether any simple name in the file still binds to something in the namespace besides the moved type.
    private static async Task<bool> StillUsesAsync(Document document, string ns, INamedTypeSymbol moved)
    {
        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        foreach (var id in root!.DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => !i.Ancestors().OfType<UsingDirectiveSyntax>().Any()))
        {
            var info = model!.GetSymbolInfo(id);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            var container = symbol switch
            {
                INamedTypeSymbol t when !SymbolEqualityComparer.Default.Equals(t, moved) => t.ContainingNamespace,
                IMethodSymbol { IsExtensionMethod: true } m => m.ContainingNamespace,
                _ => null,
            };
            if (container is not null && container.ToDisplayString() == ns)
            {
                return true;
            }
        }

        return false;
    }

    private static TextChange? UsingBlock(SyntaxNode root, SourceText text, IReadOnlyCollection<string> names, string eol)
    {
        var first = names.Select(n => NamespaceUtil.AddUsing(root, text, n, eol)).FirstOrDefault(c => c is not null);
        if (first is not { } change)
        {
            return null;
        }

        var wanted = names.Where(n => NamespaceUtil.AddUsing(root, text, n, eol) is not null).Select(n => "using " + n + ";").ToList();
        var body = string.Join(eol, wanted);
        var newText = change.NewText!.StartsWith(eol, StringComparison.Ordinal)
            ? eol + body
            : change.NewText.EndsWith(eol + eol, StringComparison.Ordinal) ? body + eol + eol : body + eol;
        return new TextChange(change.Span, newText);
    }
}
