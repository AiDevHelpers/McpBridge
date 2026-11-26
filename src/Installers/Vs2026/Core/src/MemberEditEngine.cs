using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>What the caller wants changed on an existing field or property. Only the parts that are set change.</summary>
internal sealed class MemberEdit
{
    public string? NewName { get; set; }

    public string? NewType { get; set; }

    public string? Accessibility { get; set; }

    public string? Backing { get; set; }

    public string? Setter { get; set; }

    public bool? Required { get; set; }

    public List<string> AddAttributes { get; } = new();

    public List<string> RemoveAttributes { get; } = new();

    public string? NewSummary { get; set; }

    /// <summary>The full new text of an indexer, event, event field or delegate. When set, no other part of the edit applies.</summary>
    public string? NewDeclaration { get; set; }
}

/// <summary>
/// Plans a change to an existing field or property, whatever its accessibility. A change that other code can feel (type, privacy,
/// setter, readonly, required) needs a caller edit for every reference, or it is refused; a rename updates its references itself;
/// attribute and summary changes touch no reference. Pure: the caller applies <see cref="MethodEditPlan.Result"/>.
/// </summary>
internal static class MemberEditEngine
{
    public static async Task<MethodEditPlan> PlanAsync(Solution solution, string file, int line, MemberEdit edit, IReadOnlyList<CallerEdit> callerEdits)
    {
        if (edit.NewDeclaration is not null)
        {
            return await PlanDeclarationAsync(solution, file, line, edit, callerEdits).ConfigureAwait(false);
        }

        var plan = new MethodEditPlan();
        var located = await LocateAsync(solution, file, line, plan).ConfigureAwait(false);
        if (located is null)
        {
            return plan;
        }

        var (documentId, node, symbol) = located.Value;
        var breaking = IsBreaking(edit);
        if (edit.NewName is not null && breaking)
        {
            plan.Reasons.Add(Strings.Get("MemberRenameAlone"));
            return plan;
        }

        // A rename goes through Roslyn, which rewrites every reference, so it needs no caller edits.
        var working = solution;
        if (edit.NewName is not null)
        {
            if (!SyntaxFacts.IsValidIdentifier(edit.NewName))
            {
                plan.Reasons.Add(Strings.Format("MemberBadName", edit.NewName));
                return plan;
            }

            working = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), edit.NewName).ConfigureAwait(false);
            if (plan.Warnings.Count == 0 && symbol is IPropertySymbol)
            {
                plan.Warnings.Add(Strings.Get("MemberRenameBackingField"));
            }

            located = await LocateAsync(working, file, line, plan).ConfigureAwait(false);
            if (located is null)
            {
                return plan;
            }

            (documentId, node, symbol) = located.Value;
        }

        var document = working.GetDocument(documentId)!;
        var text = await document.GetTextAsync().ConfigureAwait(false);
        var eol = text.ToString().Contains("\r\n") ? "\r\n" : "\n";

        // The references of the member as it stands, outside the member itself.
        var references = await MethodEditEngine.CollectReferencesAsync(working, symbol).ConfigureAwait(false);
        references.RemoveAll(r => r.DocumentId == documentId && node.Span.Contains(r.Span));
        plan.ReferenceCount = references.Count;
        plan.Tests.AddRange(await TestDiscovery.FindDirectAsync(working, references).ConfigureAwait(false));

        var changes = new Dictionary<DocumentId, List<TextChange>> { [documentId] = new List<TextChange>() };
        var rewrite = Rewrite(working, node, symbol, edit, eol, text, plan);
        if (plan.Refused)
        {
            return plan;
        }

        changes[documentId].AddRange(rewrite);
        if (edit.NewSummary is not null)
        {
            if (MethodDocUpdater.ReplaceSummary(node, edit.NewSummary, eol) is { } docChange)
            {
                changes[documentId].Add(docChange);
                plan.DocsUpdated = true;
            }
            else
            {
                plan.Warnings.Add(Strings.Get("MemberNoDocComment"));
            }
        }

        if (!await MethodEditEngine.AddCallerEditsAsync(working, references, callerEdits, changes, plan).ConfigureAwait(false))
        {
            return plan;
        }

        var edited = await MethodEditEngine.ApplyAsync(working, changes, plan.Divergent).ConfigureAwait(false);
        var finalName = edit.NewName ?? symbol.Name;
        var newSymbol = await FindAsync(edited, documentId, symbol.ContainingType, finalName).ConfigureAwait(false);
        if (newSymbol is null)
        {
            plan.Reasons.Add(Strings.Get("MemberNotFoundAfterEdit"));
            return plan;
        }

        plan.OldSignature = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        plan.NewSignature = newSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        plan.SignatureChanged = Key(symbol) != Key(newSymbol);
        foreach (var id in changes.Keys)
        {
            plan.Files.Add(working.GetDocument(id)!.FilePath ?? id.ToString());
        }

        if (plan.SignatureChanged)
        {
            var related = await CoverageOfReferences.RelatedAsync(working, symbol).ConfigureAwait(false);
            if (related.Count > 0)
            {
                plan.Reasons.Add(Strings.Format("MemberRelatedMembers", string.Join(", ", related)));
            }

            plan.Uncovered.AddRange(CoverageOfReferences.Uncovered(references, callerEdits));
            if (plan.Uncovered.Count > 0)
            {
                plan.Reasons.Add(Strings.Format("MemberUncovered", plan.Uncovered.Count));
            }
        }

        edited = await EditChecks.FormatFileAsync(edited, documentId, plan).ConfigureAwait(false);
        var touched = changes.Keys.Concat(references.Select(r => r.DocumentId)).Concat(RenamedDocuments(solution, working));
        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, touched, plan).ConfigureAwait(false);
        plan.Method = await LocateMemberAsync(edited, documentId, symbol.ContainingType, finalName).ConfigureAwait(false);
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    // Changes other code can feel. Attributes, the summary and the backing of a property are invisible to its users.
    // An indexer, event, event field or delegate replaced by its full new text. Its shape (kind, access, type, parameters or name) must
    // stay the same while anything refers to it; the body and accessors may change freely.
    private static async Task<MethodEditPlan> PlanDeclarationAsync(
        Solution solution, string file, int line, MemberEdit edit, IReadOnlyList<CallerEdit> callerEdits)
    {
        var plan = new MethodEditPlan();
        var target = await EditTarget.LocateAsync(solution, file, line, 0,
            new[] { typeof(IndexerDeclarationSyntax), typeof(EventDeclarationSyntax), typeof(EventFieldDeclarationSyntax), typeof(DelegateDeclarationSyntax) },
            reason => plan.Reasons.Add(reason)).ConfigureAwait(false);
        if (target is null)
        {
            return plan;
        }

        var node = (MemberDeclarationSyntax)target.Declaration;
        var parsed = SyntaxFactory.ParseMemberDeclaration(edit.NewDeclaration!.Trim());
        if (parsed is null || parsed.GetType() != node.GetType() || parsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            plan.Reasons.Add(Strings.Get("MemberEditDeclarationKind"));
            return plan;
        }

        if (parsed.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)))
        {
            plan.Reasons.Add(Strings.Get("MethodEditNewTextHasDocs"));
            return plan;
        }

        var references = await MethodEditEngine.CollectReferencesAsync(solution, target.Symbol).ConfigureAwait(false);
        references.RemoveAll(r => r.DocumentId == target.DocumentId && node.Span.Contains(r.Span));
        plan.ReferenceCount = references.Count;
        var changed = Shape(parsed) != Shape(node);
        if (changed && references.Count > 0)
        {
            plan.Reasons.Add(Strings.Format("MemberEditShapeChanged", references.Count));
            return plan;
        }

        var changes = new Dictionary<DocumentId, List<TextChange>>
        {
            [target.DocumentId] = new List<TextChange> { new TextChange(node.Span, parsed.ToString()) },
        };
        if (!await MethodEditEngine.AddCallerEditsAsync(solution, references, callerEdits, changes, plan).ConfigureAwait(false))
        {
            return plan;
        }

        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        edited = await EditChecks.FormatFileAsync(edited, target.DocumentId, plan).ConfigureAwait(false);
        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, changes.Keys.Concat(references.Select(r => r.DocumentId)), plan).ConfigureAwait(false);
        foreach (var id in changes.Keys)
        {
            plan.Files.Add(solution.GetDocument(id)!.FilePath ?? id.ToString());
        }

        plan.SignatureChanged = changed;
        plan.Tests.AddRange(await TestDiscovery.FindDirectAsync(solution, references).ConfigureAwait(false));
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    // What users of an indexer, event, event field or delegate depend on: its kind, access, type and parameters or name.
    private static string Shape(MemberDeclarationSyntax member)
    {
        var access = string.Join(" ", member.Modifiers.Select(m => m.ValueText).Where(v => v is "public" or "internal" or "protected" or "private"));
        return member switch
        {
            IndexerDeclarationSyntax i => "indexer|" + access + "|" + i.Type.WithoutTrivia() + "|" + i.ParameterList.WithoutTrivia(),
            EventDeclarationSyntax e => "event|" + access + "|" + e.Type.WithoutTrivia() + "|" + e.Identifier.ValueText,
            EventFieldDeclarationSyntax f => "eventfield|" + access + "|" + f.Declaration.Type.WithoutTrivia() + "|"
                + string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.ValueText)),
            DelegateDeclarationSyntax d => "delegate|" + access + "|" + d.ReturnType.WithoutTrivia() + "|" + d.Identifier.ValueText + "|" + d.ParameterList.WithoutTrivia(),
            _ => member.ToString(),
        };
    }

    private static bool IsBreaking(MemberEdit edit) =>
        edit.NewType is not null || edit.Accessibility is not null || edit.Setter is not null || edit.Required is not null;

    private static IEnumerable<DocumentId> RenamedDocuments(Solution before, Solution after) =>
        after.GetChanges(before).GetProjectChanges().SelectMany(p => p.GetChangedDocuments());

    private static async Task<(DocumentId Id, MemberDeclarationSyntax Node, ISymbol Symbol)?> LocateAsync(Solution solution, string file, int line, MethodEditPlan plan)
    {
        var target = await EditTarget.LocateAsync(solution, file, line, 0, [typeof(PropertyDeclarationSyntax), typeof(FieldDeclarationSyntax)],
            reason => plan.Reasons.Add(reason)).ConfigureAwait(false);
        return target is null ? null : (target.DocumentId, (MemberDeclarationSyntax)target.Declaration, target.Symbol);
    }

    private static async Task<ISymbol?> FindAsync(Solution solution, DocumentId id, INamedTypeSymbol type, string name)
    {
        var document = solution.GetDocument(id)!;
        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        var typeName = TestDiscovery.MetadataName(type);
        foreach (var node in root!.DescendantNodes())
        {
            ISymbol? symbol = node switch
            {
                PropertyDeclarationSyntax p when p.Identifier.ValueText == name => model!.GetDeclaredSymbol(p),
                VariableDeclaratorSyntax v when v.Identifier.ValueText == name && v.Parent?.Parent is FieldDeclarationSyntax => model!.GetDeclaredSymbol(v),
                _ => null,
            };
            if (symbol is not null && TestDiscovery.MetadataName(symbol.ContainingType) == typeName)
            {
                return symbol;
            }
        }

        return null;
    }

    private static async Task<object?> LocateMemberAsync(Solution solution, DocumentId id, INamedTypeSymbol type, string name)
    {
        var symbol = await FindAsync(solution, id, type, name).ConfigureAwait(false);
        var location = symbol?.Locations.FirstOrDefault(l => l.IsInSource);
        if (symbol is null || location is null)
        {
            return null;
        }

        var node = (await location.SourceTree!.GetRootAsync()).FindNode(location.SourceSpan).AncestorsAndSelf()
            .OfType<MemberDeclarationSyntax>().First(m => m is PropertyDeclarationSyntax or FieldDeclarationSyntax);
        var span = node.SyntaxTree.GetLineSpan(node.Span);
        return new
        {
            file = solution.GetDocument(id)!.FilePath,
            name,
            containing_type = TestDiscovery.MetadataName(type),
            start_line = span.StartLinePosition.Line + 1,
            end_line = span.EndLinePosition.Line + 1,
        };
    }

    // Everything about a member that its users can depend on.
    internal static string Key(ISymbol symbol)
    {
        var type = symbol switch { IPropertySymbol p => p.Type, IFieldSymbol f => f.Type, _ => null };
        var parts = new List<string>
        {
            symbol.Kind.ToString(),
            type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier)) ?? "",
            symbol.DeclaredAccessibility.ToString(),
            symbol.IsStatic.ToString(),
            symbol.IsVirtual.ToString(),
            symbol.IsAbstract.ToString(),
            symbol.IsOverride.ToString(),
        };
        switch (symbol)
        {
            case IPropertySymbol p:
                parts.Add(p.GetMethod is null ? "noget" : p.GetMethod.DeclaredAccessibility.ToString());
                parts.Add(p.SetMethod is null ? "noset" : p.SetMethod.DeclaredAccessibility + (p.SetMethod.IsInitOnly ? "init" : "set"));
                parts.Add(p.IsRequired.ToString());
                break;
            case IFieldSymbol f:
                parts.Add(f.IsReadOnly.ToString());
                parts.Add(f.IsRequired.ToString());
                break;
        }

        return string.Join("|", parts);
    }

    // The text changes for the member itself.
    private static List<TextChange> Rewrite(Solution solution, MemberDeclarationSyntax node, ISymbol symbol, MemberEdit edit, string eol, SourceText text, MethodEditPlan plan)
    {
        var result = new List<TextChange>();
        if (edit.Backing is not null)
        {
            return Rebacked(solution, node, symbol, edit, eol, text, plan);
        }

        var changed = node;
        if (edit.NewType is not null)
        {
            var type = SyntaxFactory.ParseTypeName(edit.NewType);
            if (type.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                plan.Reasons.Add(Strings.Format("MemberBadType", edit.NewType));
                return result;
            }

            changed = changed switch
            {
                PropertyDeclarationSyntax p => p.WithType(type.WithTriviaFrom(p.Type)),
                FieldDeclarationSyntax f => f.WithDeclaration(f.Declaration.WithType(type.WithTriviaFrom(f.Declaration.Type))),
                _ => changed,
            };
        }

        if (edit.Accessibility is not null || edit.Required is not null)
        {
            changed = ChangeModifiers(changed, edit, plan);
        }

        if (edit.Setter is not null)
        {
            if (changed is PropertyDeclarationSyntax property)
            {
                changed = ChangeSetter(property, edit.Setter, plan);
            }
            else
            {
                plan.Reasons.Add(Strings.Get("MemberSetterOnProperty"));
            }
        }

        if (edit.AddAttributes.Count > 0 || edit.RemoveAttributes.Count > 0)
        {
            changed = ChangeAttributes(changed, edit);
        }

        if (!plan.Refused && !ReferenceEquals(changed, node))
        {
            result.Add(new TextChange(node.Span, changed.WithoutTrivia().ToString()));
        }

        return result;
    }

    private static readonly SyntaxKind[] AccessKinds =
        { SyntaxKind.PublicKeyword, SyntaxKind.InternalKeyword, SyntaxKind.ProtectedKeyword, SyntaxKind.PrivateKeyword };

    private static MemberDeclarationSyntax ChangeModifiers(MemberDeclarationSyntax node, MemberEdit edit, MethodEditPlan plan)
    {
        var modifiers = node.Modifiers.ToList();
        if (edit.Accessibility is not null)
        {
            var words = edit.Accessibility.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var kinds = words.Select(w => SyntaxFacts.GetKeywordKind(w)).ToList();
            if (kinds.Count == 0 || kinds.Any(k => !AccessKinds.Contains(k)))
            {
                plan.Reasons.Add(Strings.Format("MemberBadAccessibility", edit.Accessibility));
                return node;
            }

            modifiers.RemoveAll(m => AccessKinds.Contains(m.Kind()));
            modifiers.InsertRange(0, kinds.Select(k => SyntaxFactory.Token(k).WithTrailingTrivia(SyntaxFactory.Space)));
        }

        if (edit.Required is { } required)
        {
            modifiers.RemoveAll(m => m.IsKind(SyntaxKind.RequiredKeyword));
            if (required)
            {
                var at = modifiers.FindLastIndex(m => AccessKinds.Contains(m.Kind())) + 1;
                modifiers.Insert(at, SyntaxFactory.Token(SyntaxKind.RequiredKeyword).WithTrailingTrivia(SyntaxFactory.Space));
            }
        }

        return node.WithModifiers(SyntaxFactory.TokenList(modifiers));
    }

    // setter: set, init, none, or an accessibility and set/init ("private set").
    private static PropertyDeclarationSyntax ChangeSetter(PropertyDeclarationSyntax property, string setter, MethodEditPlan plan)
    {
        var list = property.AccessorList;
        if (list is null)
        {
            plan.Reasons.Add(Strings.Get("MemberSetterOnAccessors"));
            return property;
        }

        var existing = list.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));
        var requested = setter.Trim();
        if (requested == "none")
        {
            return existing is null ? property : property.WithAccessorList(list.RemoveNode(existing, SyntaxRemoveOptions.KeepNoTrivia));
        }

        var words = requested.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var kind = words.Last() == "init" ? SyntaxKind.InitAccessorDeclaration : words.Last() == "set" ? SyntaxKind.SetAccessorDeclaration : SyntaxKind.None;
        var access = words.Take(words.Length - 1).Select(w => SyntaxFacts.GetKeywordKind(w)).ToList();
        if (kind == SyntaxKind.None || access.Any(k => !AccessKinds.Contains(k)))
        {
            plan.Reasons.Add(Strings.Format("MemberBadSetter", setter));
            return property;
        }

        var keyword = SyntaxFactory.Token(kind == SyntaxKind.InitAccessorDeclaration ? SyntaxKind.InitKeyword : SyntaxKind.SetKeyword);
        var modifiers = SyntaxFactory.TokenList(access.Select(k => SyntaxFactory.Token(k).WithTrailingTrivia(SyntaxFactory.Space)));
        if (existing is not null)
        {
            var replaced = SyntaxFactory.AccessorDeclaration(kind, existing.AttributeLists, modifiers, keyword.WithTriviaFrom(existing.Keyword), existing.Body, existing.ExpressionBody, existing.SemicolonToken);
            return property.WithAccessorList(list.ReplaceNode(existing, replaced));
        }

        // A missing setter on an auto property is a bare "set;"; on a property over a field it assigns that field.
        var getter = list.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var backing = getter?.ExpressionBody?.Expression.ToString();
        var added = SyntaxFactory.AccessorDeclaration(kind).WithModifiers(modifiers).WithKeyword(keyword);
        if (getter?.Body is null && getter?.ExpressionBody is null)
        {
            added = added.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }
        else if (backing is not null)
        {
            added = added.WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.ParseExpression(backing + " = value")))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }
        else
        {
            plan.Reasons.Add(Strings.Get("MemberSetterOnAccessors"));
            return property;
        }

        return property.WithAccessorList(list.AddAccessors(added));
    }

    private static MemberDeclarationSyntax ChangeAttributes(MemberDeclarationSyntax node, MemberEdit edit)
    {
        var lists = node.AttributeLists.ToList();
        foreach (var remove in edit.RemoveAttributes.Select(SimpleName))
        {
            for (var i = lists.Count - 1; i >= 0; i--)
            {
                var kept = lists[i].Attributes.Where(a => SimpleName(a.Name.ToString()) != remove).ToList();
                if (kept.Count == lists[i].Attributes.Count)
                {
                    continue;
                }

                if (kept.Count == 0)
                {
                    lists.RemoveAt(i);
                }
                else
                {
                    lists[i] = lists[i].WithAttributes(SyntaxFactory.SeparatedList(kept));
                }
            }
        }

        foreach (var add in edit.AddAttributes)
        {
            var parsed = SyntaxFactory.ParseCompilationUnit(MemberRenderer.NormalizeAttribute(add) + " class W { }");
            if (parsed.AttributeLists.Count == 0 && parsed.Members.Count > 0)
            {
                lists.AddRange(parsed.Members[0].AttributeLists);
            }
            else
            {
                lists.AddRange(parsed.AttributeLists);
            }
        }

        return node.WithAttributeLists(SyntaxFactory.List(lists.Select(l => l.WithoutTrivia().WithTrailingTrivia(SyntaxFactory.Space))));
    }

    private static string SimpleName(string name)
    {
        var trimmed = name.Trim().TrimStart('[').TrimEnd(']');
        var paren = trimmed.IndexOf('(');
        if (paren >= 0)
        {
            trimmed = trimmed.Substring(0, paren);
        }

        trimmed = trimmed.Substring(trimmed.LastIndexOf('.') + 1);
        return trimmed.EndsWith("Attribute", StringComparison.Ordinal) ? trimmed.Substring(0, trimmed.Length - 9) : trimmed;
    }

    // auto <-> field: render the member again in the other backing. The field moves with the property.
    private static List<TextChange> Rebacked(Solution solution, MemberDeclarationSyntax node, ISymbol symbol, MemberEdit edit, string eol, SourceText text, MethodEditPlan plan)
    {
        var changes = new List<TextChange>();
        if (node is not PropertyDeclarationSyntax property || edit.Backing is not ("auto" or "field"))
        {
            plan.Reasons.Add(Strings.Get("MemberBackingUnsupported"));
            return changes;
        }

        var get = property.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var set = property.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));
        var isAuto = get is not null && get.Body is null && get.ExpressionBody is null;
        var spec = new MemberSpec
        {
            Name = property.Identifier.ValueText,
            Type = property.Type.ToString(),
            Accessibility = Accessibility(property),
            Backing = edit.Backing,
            Setter = edit.Setter ?? (set is null ? "none" : (set.Modifiers.Count > 0 ? set.Modifiers + " " : "") + set.Keyword.ValueText),
            Required = property.Modifiers.Any(m => m.IsKind(SyntaxKind.RequiredKeyword)),
        };
        spec.Attributes.AddRange(property.AttributeLists.Select(l => l.ToString()));

        if (edit.Backing == "field" && isAuto)
        {
            spec.Initializer = property.Initializer?.Value.ToString();
        }
        else if (edit.Backing == "auto" && !isAuto)
        {
            var fieldName = get?.ExpressionBody?.Expression.ToString();
            var field = property.Parent is TypeDeclarationSyntax type
                ? type.Members.OfType<FieldDeclarationSyntax>().FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == fieldName))
                : null;
            if (fieldName is null || field is null || field.Declaration.Variables.Count != 1)
            {
                plan.Reasons.Add(Strings.Get("MemberBackingUnsupported"));
                return changes;
            }

            if (FieldUsedElsewhere(solution, field, property))
            {
                plan.Reasons.Add(Strings.Format("MemberBackingFieldShared", fieldName));
                return changes;
            }

            spec.Initializer = field.Declaration.Variables[0].Initializer?.Value.ToString();
            changes.Add(new TextChange(TextSpan.FromBounds(text.Lines.GetLineFromPosition(field.SpanStart).Start, text.Lines.GetLineFromPosition(field.Span.End).EndIncludingLineBreak), ""));
        }
        else
        {
            plan.Reasons.Add(Strings.Get("MemberBackingNoChange"));
            return changes;
        }

        var indent = MethodAddEngine.LeadingWhitespace(text.Lines.GetLineFromPosition(property.SpanStart).ToString());
        var rendered = MemberRenderer.Render(spec);
        changes.Add(new TextChange(property.Span, string.Join(eol, rendered.Select((l, i) => i == 0 || l.Length == 0 ? l : indent + l))));
        return changes;
    }

    private static string Accessibility(MemberDeclarationSyntax node) =>
        string.Join(" ", node.Modifiers.Where(m => AccessKinds.Contains(m.Kind())).Select(m => m.ValueText).DefaultIfEmpty("private"));

    // True when the field is read or written anywhere but inside the property that wraps it.
    private static bool FieldUsedElsewhere(Solution solution, FieldDeclarationSyntax field, PropertyDeclarationSyntax property)
    {
        var name = field.Declaration.Variables[0].Identifier.ValueText;
        var type = (TypeDeclarationSyntax)property.Parent!;
        return type.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Any(i => i.Identifier.ValueText == name && !property.Span.Contains(i.Span) && !field.Span.Contains(i.Span));
    }
}
