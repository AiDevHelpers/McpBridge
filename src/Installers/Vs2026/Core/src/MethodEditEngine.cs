using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>A file a namespace change would move so its folder matches its namespace.</summary>
internal sealed class FileMove
{
    public FileMove(string from, string to, string? project)
    {
        From = from;
        To = to;
        Project = project;
    }

    public string From { get; }

    public string To { get; }

    public string? Project { get; }
}

/// <summary>One caller edit: the reference at File:Line:Column (as <c>semanticReferences</c> returns it) gets NewText.</summary>
internal sealed class CallerEdit
{
    public CallerEdit(string file, int line, int column, string newText)
    {
        File = file;
        Line = line;
        Column = column;
        NewText = newText;
    }

    public string File { get; }

    public int Line { get; }

    public int Column { get; }

    public string NewText { get; }
}

/// <summary>The outcome of planning one method edit. <see cref="Result"/> is set only when nothing refused it.</summary>
internal sealed class MethodEditPlan
{
    public List<string> Reasons { get; } = new();

    public List<string> Warnings { get; } = new();

    public List<object> Uncovered { get; } = new();

    public List<object> NewErrors { get; } = new();

    public List<object> NewWarnings { get; } = new();

    public List<object> NewNotices { get; } = new();

    public List<object> Rebinds { get; } = new();

    public bool Formatted { get; set; }

    public int InsertedAtLine { get; set; }

    /// <summary>How many lines an insertion added, and the 1-based line it was inserted after: later lines of the file shift by it.</summary>
    public int InsertedLines { get; set; }

    public int InsertedAfterLine { get; set; }

    public List<string> Files { get; } = new();

    public bool SignatureChanged { get; set; }

    public string OldSignature { get; set; } = "";

    public string NewSignature { get; set; } = "";

    public int ReferenceCount { get; set; }

    public bool DocsUpdated { get; set; }

    /// <summary>The fingerprint of what this plan changes (<see cref="PlanHash"/>). Set by the bridge before the reply is written.</summary>
    public string? Hash { get; set; }

    /// <summary>Test methods that call the edited method, found before the edit. The MCP server runs them after an applied edit.</summary>
    public List<TestTarget> Tests { get; } = new();

    /// <summary>Where the edited, added or overloaded method sits in its file after the edit, for coverage.</summary>
    public object? Method { get; set; }

    /// <summary>The method an add or overload plan inserts, and whether it is a test method.</summary>
    public TestTarget? Added { get; set; }

    public bool AddedIsTest { get; set; }

    /// <summary>Names of the fields and properties added alongside the method.</summary>
    public List<string> MembersAdded { get; } = new();

    /// <summary>Files a namespace change would move so their folder matches (from, to), and string literals that still mention the old name.</summary>
    public List<FileMove> FileMoves { get; } = new();

    public List<object> StringMentions { get; } = new();

    public int DeclarationCount { get; set; }

    public Solution? Result { get; set; }

    public bool Refused => Reasons.Count > 0;

    public object ToReply() => new
    {
        refused = Refused,
        reasons = Reasons,
        signature_changed = SignatureChanged,
        old_signature = OldSignature,
        new_signature = NewSignature,
        reference_count = ReferenceCount,
        docs_updated = DocsUpdated,
        plan_hash = Hash,
        tests = Tests.Select(t => t.ToReply()).ToList(),
        method = Method,
        uncovered_references = Uncovered,
        new_errors = NewErrors,
        new_warnings = NewWarnings,
        new_notices = NewNotices,
        rebound_references = Rebinds,
        formatted = Formatted,
        inserted_at_line = InsertedAtLine,
        members_added = MembersAdded,
        file_moves = FileMoves.Select(m => new { from = m.From, to = m.To }).ToList(),
        string_mentions = StringMentions,
        declaration_count = DeclarationCount,
        files = Files,
        warnings = Warnings,
    };
}

/// <summary>
/// Plans an edit of one C# method on a Roslyn <see cref="Solution"/>: replaces the method, applies the caller edits, compares the
/// old and new method symbols, and refuses unless every reference is covered and nothing new fails to compile. Pure: it changes
/// nothing; the caller applies <see cref="MethodEditPlan.Result"/>.
/// </summary>
internal static class MethodEditEngine
{
    private const int MaxReported = 20;

    private static readonly SymbolDisplayFormat KeyFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static async Task<MethodEditPlan> PlanAsync(
        Solution solution, string file, int line, int column, string newMethodText, IReadOnlyList<CallerEdit> callerEdits, DocUpdates? docs = null, Solution? baseline = null)
    {
        var plan = new MethodEditPlan();

        var documentId = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault();
        var document = documentId is null ? null : solution.GetDocument(documentId);
        if (document is null)
        {
            plan.Reasons.Add(Strings.Format("NoRoslynDocumentFor", file));
            return plan;
        }

        var text = await document.GetTextAsync().ConfigureAwait(false);
        if (line < 1 || line > text.Lines.Count)
        {
            plan.Reasons.Add(Strings.Format("LineOutOfRange", line, file));
            return plan;
        }

        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        var position = Math.Min(text.Lines[line - 1].Start + Math.Max(0, column - 1), text.Length);
        var methodNode = root?.FindToken(position).Parent?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        var symbol = methodNode is null || model is null ? null : model.GetDeclaredSymbol(methodNode);
        if (methodNode is null || symbol is null)
        {
            plan.Reasons.Add(Strings.Get("MethodEditNotAMethod"));
            return plan;
        }

        var parsed = SyntaxFactory.ParseMemberDeclaration(newMethodText);
        if (parsed is not MethodDeclarationSyntax || parsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            plan.Reasons.Add(Strings.Get("MethodEditNewTextNotOneMethod"));
            return plan;
        }

        if (parsed.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)))
        {
            plan.Reasons.Add(Strings.Get("MethodEditNewTextHasDocs"));
            return plan;
        }

        // The references of the old symbol, before any edit. References inside the method itself are covered by its new text.
        var methodSpan = methodNode.Span;
        var references = await CollectReferencesAsync(solution, symbol).ConfigureAwait(false);
        references.RemoveAll(r => r.DocumentId == documentId && methodSpan.Contains(r.Span));
        plan.ReferenceCount = references.Count;
        plan.Tests.AddRange(await TestDiscovery.FindDirectAsync(solution, references).ConfigureAwait(false));

        var changes = new Dictionary<DocumentId, List<TextChange>>
        {
            [documentId!] = new List<TextChange> { new TextChange(methodSpan, newMethodText) },
        };

        if (!await AddCallerEditsAsync(solution, references, callerEdits, changes, plan).ConfigureAwait(false))
        {
            return plan;
        }

        var edited = await ApplyAsync(solution, changes).ConfigureAwait(false);
        var newSymbol = await FindEditedMethodAsync(edited, documentId!, methodSpan.Start, changes[documentId!]).ConfigureAwait(false);
        if (newSymbol is null)
        {
            plan.Reasons.Add(Strings.Get("MethodEditNewMethodNotFound"));
            return plan;
        }

        plan.OldSignature = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        plan.NewSignature = newSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        plan.SignatureChanged = SignatureKey(symbol) != SignatureKey(newSymbol);

        // The XML doc comment follows the signature, in the layout it already has. Only when something changed or the caller asked.
        if (plan.SignatureChanged || docs?.NewSummary is not null || docs?.Items.Count > 0)
        {
            var eol = text.ToString().Contains("\r\n") ? "\r\n" : "\n";
            var docResult = MethodDocUpdater.Update(methodNode, symbol, newSymbol, docs ?? new DocUpdates(), plan.SignatureChanged, eol);
            plan.Warnings.AddRange(docResult.Warnings);
            if (docResult.Missing.Count > 0)
            {
                plan.Reasons.Add(Strings.Format("MethodEditDocsMissing", string.Join(", ", docResult.Missing)));
            }
            else if (docResult.Change is { } docChange)
            {
                changes[documentId!].Add(docChange);
                edited = await ApplyAsync(solution, changes).ConfigureAwait(false);
                plan.DocsUpdated = true;
            }
        }

        foreach (var id in changes.Keys)
        {
            plan.Files.Add(solution.GetDocument(id)!.FilePath ?? id.ToString());
        }

        if (plan.SignatureChanged)
        {
            await CheckSignatureChangeAsync(solution, symbol, references, callerEdits, plan).ConfigureAwait(false);
        }

        // Format the method's file, then report what the edit introduced (errors refuse; warnings and notices are reported).
        edited = await EditChecks.FormatFileAsync(edited, documentId!, plan).ConfigureAwait(false);
        await EditChecks.CheckNewDiagnosticsAsync(baseline ?? solution, edited, changes.Keys.Concat(references.Select(r => r.DocumentId)), plan)
            .ConfigureAwait(false);
        plan.Method = await TestDiscovery.LocateAsync(edited, documentId!, TestDiscovery.MetadataName(newSymbol.ContainingType), SignatureKey(newSymbol))
            .ConfigureAwait(false);

        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (references.Any(r => r.IsCandidate || r.IsImplicit))
        {
            plan.Warnings.Add(Strings.Get("MethodEditWarnCandidates"));
        }

        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    // The node a reference token belongs to: the whole invocation when the token names the called method, else the token's parent.
    internal static SyntaxNode UseNode(SyntaxToken token)
    {
        for (var node = token.Parent; node is not null && node is not StatementSyntax && node is not MemberDeclarationSyntax; node = node.Parent)
        {
            if (node is InvocationExpressionSyntax invocation && invocation.Expression.Span.End == token.Span.End)
            {
                return invocation;
            }
        }

        return token.Parent!;
    }

    internal static async Task<Solution> ApplyAsync(Solution solution, Dictionary<DocumentId, List<TextChange>> changes)
    {
        var edited = solution;
        foreach (var pair in changes)
        {
            var oldText = await solution.GetDocument(pair.Key)!.GetTextAsync().ConfigureAwait(false);
            edited = edited.WithDocumentText(pair.Key, oldText.WithChanges(pair.Value));
        }

        return edited;
    }

    internal sealed class Reference
    {
        public DocumentId DocumentId { get; set; } = null!;

        public string File { get; set; } = "";

        public int Line { get; set; }

        public int Column { get; set; }

        public TextSpan Span { get; set; }

        public bool IsImplicit { get; set; }

        public bool IsCandidate { get; set; }
    }

    internal static async Task<List<Reference>> CollectReferencesAsync(Solution solution, ISymbol symbol)
    {
        var found = new List<Reference>();
        var seen = new HashSet<(DocumentId, int)>();
        foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbol, solution).ConfigureAwait(false))
        {
            foreach (var location in referenced.Locations)
            {
                var span = location.Location.GetLineSpan();
                if (seen.Add((location.Document.Id, location.Location.SourceSpan.Start)))
                {
                    found.Add(new Reference
                    {
                        DocumentId = location.Document.Id,
                        File = span.Path,
                        Line = span.StartLinePosition.Line + 1,
                        Column = span.StartLinePosition.Character + 1,
                        Span = location.Location.SourceSpan,
                        IsImplicit = location.IsImplicit,
                        IsCandidate = location.IsCandidateLocation,
                    });
                }
            }
        }

        return found;
    }

    // Each caller edit must sit on a known reference and replace the invocation that holds it (or the bare identifier for a method
    // group or nameof). Edits that overlap each other or the method are refused.
    internal static async Task<bool> AddCallerEditsAsync(
        Solution solution, List<Reference> references, IReadOnlyList<CallerEdit> callerEdits,
        Dictionary<DocumentId, List<TextChange>> changes, MethodEditPlan plan)
    {
        foreach (var edit in callerEdits)
        {
            var reference = references.FirstOrDefault(r =>
                string.Equals(r.File, edit.File, StringComparison.OrdinalIgnoreCase) && r.Line == edit.Line && r.Column == edit.Column);
            if (reference is null)
            {
                plan.Reasons.Add(Strings.Format("MethodEditNotAReference", edit.File, edit.Line, edit.Column));
                continue;
            }

            var document = solution.GetDocument(reference.DocumentId)!;
            var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
            var token = root!.FindToken(reference.Span.Start);
            var span = token.Span;
            for (var node = token.Parent; node is not null && node is not StatementSyntax && node is not MemberDeclarationSyntax; node = node.Parent)
            {
                if (node is InvocationExpressionSyntax invocation && invocation.Expression.Span.End == token.Span.End)
                {
                    span = invocation.Span;
                    break;
                }
            }

            if (!changes.TryGetValue(reference.DocumentId, out var list))
            {
                changes[reference.DocumentId] = list = new List<TextChange>();
            }

            if (list.Any(c => c.Span.OverlapsWith(span)))
            {
                plan.Reasons.Add(Strings.Format("MethodEditOverlap", edit.File, edit.Line, edit.Column));
                continue;
            }

            list.Add(new TextChange(span, edit.NewText));
        }

        return !plan.Refused;
    }

    // Finds the method that now sits where the old one began, allowing for caller edits earlier in the same file.
    private static async Task<IMethodSymbol?> FindEditedMethodAsync(Solution edited, DocumentId id, int oldStart, List<TextChange> changes)
    {
        var shift = changes.Where(c => c.Span.End <= oldStart).Sum(c => c.NewText!.Length - c.Span.Length);
        var document = edited.GetDocument(id)!;
        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
        var node = root?.FindToken(Math.Min(oldStart + shift, root.FullSpan.End)).Parent?.AncestorsAndSelf()
            .OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return node is null || model is null ? null : model.GetDeclaredSymbol(node);
    }

    // A signature change with callers needs every reference covered. Overrides and implementations are not edited by this tool,
    // so a signature change on a member that has them is refused.
    private static async Task CheckSignatureChangeAsync(
        Solution solution, IMethodSymbol symbol, List<Reference> references, IReadOnlyList<CallerEdit> callerEdits, MethodEditPlan plan)
    {
        var related = new List<string>();
        if (symbol.OverriddenMethod is not null)
        {
            related.Add(symbol.OverriddenMethod.ToDisplayString());
        }

        foreach (var iface in symbol.ContainingType.AllInterfaces.SelectMany(i => i.GetMembers().OfType<IMethodSymbol>()))
        {
            if (SymbolEqualityComparer.Default.Equals(symbol.ContainingType.FindImplementationForInterfaceMember(iface), symbol))
            {
                related.Add(iface.ToDisplayString());
            }
        }

        if (symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride)
        {
            foreach (var overrider in await SymbolFinder.FindOverridesAsync(symbol, solution).ConfigureAwait(false))
            {
                related.Add(overrider.ToDisplayString());
            }
        }

        if (related.Count > 0)
        {
            plan.Reasons.Add(Strings.Format("MethodEditRelatedMembers", string.Join(", ", related.Distinct().Take(MaxReported))));
        }

        plan.Uncovered.AddRange(CoverageOfReferences.Uncovered(references, callerEdits));
        if (plan.Uncovered.Count > 0)
        {
            plan.Reasons.Add(Strings.Format("MethodEditUncovered", plan.Uncovered.Count));
        }
    }


    // Everything about a method that a caller can depend on. Async, attributes and the body are left out on purpose.
    internal static string SignatureKey(IMethodSymbol method)
    {
        var typeParameters = string.Join(",", method.TypeParameters.Select(p =>
            p.Name + ":" + p.HasReferenceTypeConstraint + p.HasValueTypeConstraint + p.HasNotNullConstraint + p.HasConstructorConstraint
            + string.Join("&", p.ConstraintTypes.Select(t => t.ToDisplayString(KeyFormat)))));
        var parameters = string.Join(",", method.Parameters.Select(p =>
            p.RefKind + (p.IsParams ? " params " : " ") + p.Type.ToDisplayString(KeyFormat) + " " + p.Name
            + (p.HasExplicitDefaultValue ? "=" + p.ExplicitDefaultValue : "")));
        return string.Join("|", method.Name, method.Arity, typeParameters, method.ReturnType.ToDisplayString(KeyFormat), method.RefKind,
            method.DeclaredAccessibility, method.IsStatic, method.IsVirtual, method.IsAbstract, method.IsOverride, method.IsSealed,
            method.IsExtensionMethod, parameters);
    }
}
