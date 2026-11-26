using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Conversions of one member's shape (<c>ide_convert</c>, units 7.1 to 7.3 and 7.6). Each kind is one rewriter with its own refusals:
/// "field-to-property" encapsulates a private field as a property of the same name and redirects its uses; "null-guards" adds an
/// argument check for each reference-typed parameter at the top of a method; "expression-to-block" and "block-to-expression" convert a
/// property or method body. Pure: the caller applies <see cref="MethodEditPlan.Result"/>.
/// </summary>
internal static class ConvertEngine
{
    public static async Task<MethodEditPlan> PlanAsync(Solution solution, string file, int line, string kind)
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
        var lineText = text.Lines[line - 1].ToString();
        var position = text.Lines[line - 1].Start + (lineText.Length - lineText.TrimStart().Length);
        var node = root!.FindToken(Math.Min(position, root.FullSpan.End)).Parent;
        var eol = text.ToString().Contains("\r\n") ? "\r\n" : "\n";
        switch (kind)
        {
            case "field-to-property":
                return await FieldToPropertyAsync(solution, documentId!, document, text, root, model!, node, eol, plan).ConfigureAwait(false);
            case "null-guards":
                return await NullGuards(solution, documentId!, document, text, node, eol, plan).ConfigureAwait(false);
            case "expression-to-block":
            case "block-to-expression":
                return await BodyShapeAsync(solution, documentId!, document, text, model!, node, kind == "block-to-expression", plan).ConfigureAwait(false);
            case "var-to-explicit":
            case "explicit-to-var":
                return await LocalTypeAsync(solution, documentId!, document, text, model!, node, kind == "explicit-to-var", plan).ConfigureAwait(false);
            default:
                plan.Reasons.Add(Strings.Format("ConvertBadKind", kind));
                return plan;
        }
    }

    private static async Task<MethodEditPlan> FieldToPropertyAsync(
        Solution solution, DocumentId documentId, Document document, SourceText text, SyntaxNode root, SemanticModel model, SyntaxNode? node, string eol, MethodEditPlan plan)
    {
        var field = node?.AncestorsAndSelf().OfType<FieldDeclarationSyntax>().FirstOrDefault();
        var declarator = field?.Declaration.Variables.FirstOrDefault();
        if (declarator is null || field is null || model.GetDeclaredSymbol(declarator) is not IFieldSymbol symbol)
        {
            plan.Reasons.Add(Strings.Get("ConvertNotAField"));
            return plan;
        }

        if (field.Declaration.Variables.Count != 1 || symbol.IsStatic || symbol.IsReadOnly || symbol.IsConst)
        {
            plan.Reasons.Add(Strings.Get("ConvertFieldShape"));
            return plan;
        }

        var name = declarator.Identifier.ValueText;
        var propertyName = name.TrimStart('_');
        if (propertyName.Length == 0)
        {
            plan.Reasons.Add(Strings.Get("ConvertFieldShape"));
            return plan;
        }

        propertyName = char.ToUpperInvariant(propertyName[0]) + propertyName.Substring(1);
        if (symbol.ContainingType.GetMembers(propertyName).Any())
        {
            plan.Reasons.Add(Strings.Format("MemberExists", propertyName));
            return plan;
        }

        var access = field.Modifiers.Select(m => m.ValueText).Where(v => v is "public" or "internal" or "private" or "protected").ToList();
        var type = field.Declaration.Type.WithoutTrivia().ToString();
        var initializer = declarator.Initializer is null ? "" : " " + declarator.Initializer.WithoutTrivia().ToString().TrimEnd(';') + ";";
        var property = (access.Count > 0 ? string.Join(" ", access) + " " : "") + type + " " + propertyName + " { get; set; }" + initializer;
        var changes = new Dictionary<DocumentId, List<TextChange>>();
        void Add(DocumentId id, TextChange change)
        {
            if (!changes.TryGetValue(id, out var list))
            {
                changes[id] = list = new List<TextChange>();
            }

            list.Add(change);
        }

        // Every use of the field becomes the property. A use as ref or out argument would need a property that is not a variable; refused.
        var references = await MethodEditEngine.CollectReferencesAsync(solution, symbol).ConfigureAwait(false);
        var declarationSpan = field.FullSpan;
        foreach (var reference in references.Where(r => !(r.DocumentId == documentId && declarationSpan.Contains(r.Span))))
        {
            var refDocument = solution.GetDocument(reference.DocumentId);
            var refRoot = refDocument is null ? null : await refDocument.GetSyntaxRootAsync().ConfigureAwait(false);
            var token = refRoot?.FindToken(reference.Span.Start);
            var refArgument = token?.Parent?.Ancestors().OfType<ArgumentSyntax>().FirstOrDefault();
            if (refArgument is not null && refArgument.RefOrOutKeyword.Kind() != SyntaxKind.None)
            {
                plan.Reasons.Add(Strings.Get("ConvertFieldRefUse"));
                return plan;
            }

            Add(reference.DocumentId, new TextChange(reference.Span, propertyName));
        }

        Add(documentId, new TextChange(field.FullSpan, property + eol));
        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        foreach (var id in changes.Keys)
        {
            edited = await EditChecks.FormatFileAsync(edited, id, plan).ConfigureAwait(false);
        }

        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, changes.Keys, plan).ConfigureAwait(false);
        plan.Files.AddRange(changes.Keys.Select(id => solution.GetDocument(id)!.FilePath ?? id.ToString()));
        plan.Warnings.Add(Strings.Format("ConvertFieldDone", name, propertyName, references.Count));
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    private static async Task<MethodEditPlan> NullGuards(Solution solution, DocumentId documentId, Document document, SourceText text, SyntaxNode? node, string eol, MethodEditPlan plan)
    {
        var method = node?.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        if (method is null)
        {
            plan.Reasons.Add(Strings.Get("ConvertNotAMethod"));
            return plan;
        }

        var guards = method.ParameterList.Parameters
            .Where(p => p.Type is not null && !p.Type.ToString().EndsWith("?", StringComparison.Ordinal) && !IsValueType(p.Type))
            .Select(p => p.Identifier.ValueText)
            .ToList();
        if (guards.Count == 0)
        {
            plan.Reasons.Add(Strings.Get("ConvertNoGuardsNeeded"));
            return plan;
        }

        if (method.Body is not BlockSyntax block)
        {
            plan.Reasons.Add(Strings.Get("ConvertNoBody"));
            return plan;
        }

        var methodIndent = MethodAddEngine.LeadingWhitespace(text.Lines.GetLineFromPosition(method.SpanStart).ToString());
        var bodyIndent = methodIndent + "    ";
        var guardLines = guards.Select(g => bodyIndent + "if (" + g + " == null) throw new System.ArgumentNullException(nameof(" + g + "));").ToList();
        var change = new TextChange(new TextSpan(block.OpenBraceToken.Span.End, 0), eol + string.Join(eol, guardLines));
        var changes = new Dictionary<DocumentId, List<TextChange>> { [documentId] = new List<TextChange> { change } };
        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        edited = await EditChecks.FormatFileAsync(edited, documentId, plan).ConfigureAwait(false);
        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, new[] { documentId }, plan).ConfigureAwait(false);
        plan.Files.Add(document.FilePath ?? "");
        plan.Warnings.Add(Strings.Format("ConvertGuardsDone", string.Join(", ", guards)));
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    // A method between an expression body and a block: "=> e;" becomes "{ return e; }" (or "{ e; }" when void), and a block of one return
    // statement becomes "=> e;". Anything else is refused.
    private static async Task<MethodEditPlan> BodyShapeAsync(
        Solution solution, DocumentId documentId, Document document, SourceText text, SemanticModel model, SyntaxNode? node, bool toExpression, MethodEditPlan plan)
    {
        var method = node?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (method is null || model.GetDeclaredSymbol(method) is not IMethodSymbol symbol)
        {
            plan.Reasons.Add(Strings.Get("ConvertNotAMethod"));
            return plan;
        }

        TextChange change;
        if (toExpression)
        {
            if (method.Body is not { Statements.Count: 1 } block || block.Statements[0] is not ReturnStatementSyntax { Expression: { } value } || symbol.ReturnsVoid)
            {
                plan.Reasons.Add(Strings.Get("ConvertNotOneReturn"));
                return plan;
            }

            change = new TextChange(method.Body!.Span, "=> " + value.WithoutTrivia() + ";");
        }
        else
        {
            if (method.ExpressionBody is null || method.SemicolonToken.IsKind(SyntaxKind.None))
            {
                plan.Reasons.Add(Strings.Get("ConvertNotExpressionBody"));
                return plan;
            }

            var expression = method.ExpressionBody.Expression.WithoutTrivia().ToString();
            var statement = symbol.ReturnsVoid ? expression + ";" : "return " + expression + ";";
            change = new TextChange(TextSpan.FromBounds(method.ExpressionBody.SpanStart, method.SemicolonToken.Span.End), "{ " + statement + " }");
        }

        var changes = new Dictionary<DocumentId, List<TextChange>> { [documentId] = new List<TextChange> { change } };
        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        edited = await EditChecks.FormatFileAsync(edited, documentId, plan).ConfigureAwait(false);
        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, new[] { documentId }, plan).ConfigureAwait(false);
        plan.Files.Add(document.FilePath ?? "");
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    // "var x = e;" becomes the explicit type of e, or an explicit type whose initializer has exactly that type becomes "var". Anything
    // else (anonymous types, no initializer, a different type) is refused.
    private static async Task<MethodEditPlan> LocalTypeAsync(
        Solution solution, DocumentId documentId, Document document, SourceText text, SemanticModel model, SyntaxNode? node, bool toVar, MethodEditPlan plan)
    {
        var declaration = node?.AncestorsAndSelf().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault()?.Declaration;
        var variable = declaration?.Variables.FirstOrDefault();
        if (declaration is null || variable?.Initializer is null)
        {
            plan.Reasons.Add(Strings.Get("ConvertNotALocal"));
            return plan;
        }

        var initializer = variable.Initializer.Value;
        var initType = model.GetTypeInfo(initializer).Type;
        if (initType is null || initType.IsAnonymousType || initType.TypeKind == TypeKind.Error)
        {
            plan.Reasons.Add(Strings.Get("ConvertNoNameableType"));
            return plan;
        }

        TextChange change;
        if (toVar)
        {
            var declared = model.GetTypeInfo(declaration.Type).Type;
            if (declaration.Type.IsVar || declared is null || !SymbolEqualityComparer.Default.Equals(declared, initType))
            {
                plan.Reasons.Add(Strings.Get("ConvertNotSameType"));
                return plan;
            }

            change = new TextChange(declaration.Type.Span, "var");
        }
        else
        {
            if (!declaration.Type.IsVar)
            {
                plan.Reasons.Add(Strings.Get("ConvertAlreadyExplicit"));
                return plan;
            }

            change = new TextChange(declaration.Type.Span, initType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        }

        var changes = new Dictionary<DocumentId, List<TextChange>> { [documentId] = new List<TextChange> { change } };
        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        edited = await EditChecks.FormatFileAsync(edited, documentId, plan).ConfigureAwait(false);
        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, new[] { documentId }, plan).ConfigureAwait(false);
        plan.Files.Add(document.FilePath ?? "");
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    private static bool IsValueType(TypeSyntax type) => type is PredefinedTypeSyntax predefined && !predefined.Keyword.IsKind(SyntaxKind.StringKeyword)
        && !predefined.Keyword.IsKind(SyntaxKind.ObjectKeyword);
}
