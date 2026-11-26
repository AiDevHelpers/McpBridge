using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Plans renaming a namespace and the namespaces under it across the solution: every declaration, using, qualified
/// name, alias and cref that spells it out. A reference written relative to an enclosing namespace cannot be rewritten safely and
/// needs a caller edit. Pure: it changes nothing; the caller applies <see cref="MethodEditPlan.Result"/>.
/// </summary>
internal static class NamespaceEngine
{
    private const int MaxReported = 50;

    public static async Task<MethodEditPlan> RenameAsync(
        Solution solution, string oldName, string newName, bool moveFiles, IReadOnlyList<CallerEdit> callerEdits)
    {
        var plan = new MethodEditPlan { OldSignature = oldName, NewSignature = newName };
        if (!NamespaceUtil.IsValidQualified(oldName) || !NamespaceUtil.IsValidQualified(newName))
        {
            plan.Reasons.Add(Strings.Format("NamespaceBadName", NamespaceUtil.IsValidQualified(oldName) ? newName : oldName));
            return plan;
        }

        if (oldName == newName)
        {
            plan.Reasons.Add(Strings.Get("NamespaceSame"));
            return plan;
        }

        var changes = new Dictionary<DocumentId, List<TextChange>>();
        var declared = new HashSet<string>(StringComparer.Ordinal);
        string Map(string full) => newName + full.Substring(oldName.Length);
        bool InScope(string full) => full == oldName || full.StartsWith(oldName + ".", StringComparison.Ordinal);

        // 1. Declarations, found from the syntax: `namespace A.B` or `namespace A.B;`.
        var affectedDocuments = new List<Document>();
        foreach (var document in solution.Projects.SelectMany(p => p.Documents))
        {
            var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
            if (root is null)
            {
                continue;
            }

            foreach (var declaration in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
            {
                var full = NamespaceUtil.FullName(declaration);
                if (!InScope(full))
                {
                    continue;
                }

                if (declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Any())
                {
                    plan.Reasons.Add(Strings.Format("NamespaceNestedBlock", document.FilePath, declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                    continue;
                }

                declared.Add(full);
                Add(changes, document.Id, new TextChange(declaration.Name.Span, Map(full)));
                if (!affectedDocuments.Contains(document))
                {
                    affectedDocuments.Add(document);
                }
            }
        }

        if (plan.Refused)
        {
            return plan;
        }

        if (declared.Count == 0)
        {
            plan.Reasons.Add(Strings.Format("NamespaceNotDeclared", oldName));
            return plan;
        }

        plan.DeclarationCount = changes.Values.Sum(l => l.Count);

        // 2. Sanity: not declared in a referenced assembly, no type collides, merging is reported.
        var symbols = new List<INamespaceSymbol>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            foreach (var full in declared)
            {
                var symbol = Find(compilation.GlobalNamespace, full);
                if (symbol is null)
                {
                    continue;
                }

                if (symbol.Locations.Any(l => l.IsInMetadata))
                {
                    plan.Reasons.Add(Strings.Format("NamespaceInMetadata", full));
                }

                symbols.Add(symbol);
                CheckCollisions(compilation, symbol, full, Map(full), plan);
            }

            var target = Find(compilation.GlobalNamespace, newName);
            if (target is not null && target.Locations.Any(l => l.IsInSource) && !plan.Warnings.Contains(Strings.Format("NamespaceMerges", newName)))
            {
                plan.Warnings.Add(Strings.Format("NamespaceMerges", newName));
            }
        }

        if (plan.Refused)
        {
            return plan;
        }

        // 3. References that spell the namespace out, from the declared names up to the leftmost written segment.
        var references = new Dictionary<(DocumentId, int), (TextSpan Span, string Written, string Symbol, TextSpan Segment, string File, int Line, int Column)>();
        foreach (var symbol in symbols)
        {
            var full = symbol.ToDisplayString();
            foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbol, solution).ConfigureAwait(false))
            {
                foreach (var location in referenced.Locations)
                {
                    var root = await location.Document.GetSyntaxRootAsync().ConfigureAwait(false);
                    var token = root!.FindToken(location.Location.SourceSpan.Start);
                    if (!token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken))
                    {
                        continue;
                    }

                    var (span, written) = NamespaceUtil.ChainOf(token);
                    var line = location.Location.GetLineSpan();
                    var key = (location.Document.Id, span.Start);
                    if (!references.TryGetValue(key, out var existing) || existing.Span.End < span.End)
                    {
                        references[key] = (span, written, full, location.Location.SourceSpan, line.Path, line.StartLinePosition.Line + 1, line.StartLinePosition.Character + 1);
                    }
                }
            }
        }

        plan.ReferenceCount = references.Count;
        foreach (var pair in references.OrderBy(p => p.Key.Item2))
        {
            var (id, _) = pair.Key;
            var (span, written, full, _, file, line, column) = pair.Value;
            if (changes.TryGetValue(id, out var existing) && existing.Any(c => c.Span.OverlapsWith(span)))
            {
                continue;
            }

            var caller = callerEdits.FirstOrDefault(e => string.Equals(e.File, file, StringComparison.OrdinalIgnoreCase) && e.Line == line && e.Column == column);
            if (caller is not null)
            {
                Add(changes, id, new TextChange(span, caller.NewText));
            }
            else if (written == full)
            {
                Add(changes, id, new TextChange(span, Map(full)));
            }
            else
            {
                plan.Uncovered.Add(new { file, line, column, written, namespace_name = full });
            }
        }

        if (plan.Uncovered.Count > 0)
        {
            plan.Reasons.Add(Strings.Format("NamespaceRelativeReferences", plan.Uncovered.Count));
            return plan;
        }

        // 4. Strings that still mention the old name, and the folder convention.
        foreach (var document in solution.Projects.SelectMany(p => p.Documents))
        {
            var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
            if (root is not null && plan.StringMentions.Count < MaxReported)
            {
                plan.StringMentions.AddRange(NamespaceUtil.StringMentions(document, root, oldName).Take(MaxReported - plan.StringMentions.Count));
            }
        }

        if (plan.StringMentions.Count > 0)
        {
            plan.Warnings.Add(Strings.Format("NamespaceStringMentions", plan.StringMentions.Count));
        }

        await PlanFileMovesAsync(affectedDocuments, Map, plan, moveFiles).ConfigureAwait(false);

        // 5. Apply, then report what the change broke or newly warns about.
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

    internal static void Add(Dictionary<DocumentId, List<TextChange>> changes, DocumentId id, TextChange change)
    {
        if (!changes.TryGetValue(id, out var list))
        {
            changes[id] = list = new List<TextChange>();
        }

        list.Add(change);
    }

    internal static INamespaceSymbol? Find(INamespaceSymbol root, string fullName)
    {
        var current = root;
        foreach (var part in fullName.Split('.'))
        {
            current = current.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    // A type that would land on a name that already exists.
    private static void CheckCollisions(Compilation compilation, INamespaceSymbol symbol, string oldFull, string newFull, MethodEditPlan plan)
    {
        foreach (var type in symbol.GetTypeMembers())
        {
            var existing = compilation.GetTypeByMetadataName(newFull + "." + type.MetadataName);
            if (existing is not null && !SymbolEqualityComparer.Default.Equals(existing, type))
            {
                plan.Reasons.Add(Strings.Format("NamespaceCollision", type.Name, newFull));
            }
        }
    }

    // The files whose folder no longer matches the new namespace, when the project follows the folder convention.
    private static async Task PlanFileMovesAsync(IEnumerable<Document> documents, Func<string, string> map, MethodEditPlan plan, bool moveFiles)
    {
        var mismatched = 0;
        foreach (var group in documents.GroupBy(d => d.Project))
        {
            var project = group.Key;
            if (project.FilePath is null || !await NamespaceUtil.FollowsConventionAsync(project).ConfigureAwait(false))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(project.FilePath)!;
            foreach (var document in group)
            {
                var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
                var declaration = root!.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
                if (declaration is null || document.FilePath is null)
                {
                    continue;
                }

                var folder = NamespaceUtil.FolderFor(directory, project.DefaultNamespace, map(NamespaceUtil.FullName(declaration)));
                if (folder is null || string.Equals(Path.GetDirectoryName(document.FilePath), folder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                mismatched++;
                if (moveFiles)
                {
                    plan.FileMoves.Add(new FileMove(document.FilePath, Path.Combine(folder, Path.GetFileName(document.FilePath)), project.FilePath));
                }
            }
        }

        if (mismatched > 0 && !moveFiles)
        {
            plan.Warnings.Add(Strings.Format("NamespaceFolderMismatch", mismatched));
        }
    }
}
