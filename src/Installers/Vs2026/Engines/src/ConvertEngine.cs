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
    public static async Task<MethodEditPlan> PlanAsync(Solution solution, string file, int line, string kind, int depth = 0, bool suffix = false)
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
                return await NullGuardsAsync(solution, documentId!, document, text, node, eol, plan).ConfigureAwait(false);
            case "expression-to-block":
            case "block-to-expression":
                return await BodyShapeAsync(solution, documentId!, document, text, model!, node, kind == "block-to-expression", plan).ConfigureAwait(false);
            case "var-to-explicit":
            case "explicit-to-var":
                return await LocalTypeAsync(solution, documentId!, document, text, model!, node, kind == "explicit-to-var", plan).ConfigureAwait(false);
            case "string-to-interpolation":
                return await StringToInterpolationAsync(solution, documentId!, document, text, model!, root!, line, plan).ConfigureAwait(false);
            case "class-to-record":
                return await ClassToRecordAsync(solution, documentId!, document, text, model!, node, "record", plan).ConfigureAwait(false);
            case "class-to-struct":
                return await ClassToRecordAsync(solution, documentId!, document, text, model!, node, "struct", plan).ConfigureAwait(false);
            case "var-to-explicit-file":
            case "string-to-interpolation-file":
                return await FileWideAsync(solution, documentId!, document, text, model!, root!, kind, plan).ConfigureAwait(false);
            case "auto-to-expression":
            case "expression-to-auto":
                return await PropertyBodyAsync(solution, documentId!, document, text, model!, node, kind == "auto-to-expression", plan).ConfigureAwait(false);
            case "make-async":
                return await AsyncEngine.PlanAsync(solution, file, line, depth, suffix).ConfigureAwait(false);
            case "property-to-method":
            case "method-to-property":
                return await PropertyMethodAsync(solution, documentId!, document, text, model!, node, kind == "property-to-method", plan).ConfigureAwait(false);
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
            if (refArgument is not null && !refArgument.RefOrOutKeyword.IsKind(SyntaxKind.None))
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

    private static async Task<MethodEditPlan> NullGuardsAsync(Solution solution, DocumentId documentId, Document document, SourceText text, SyntaxNode? node, string eol, MethodEditPlan plan)
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

    private static async Task<MethodEditPlan> ApplyAndCheckAsync(Solution solution, DocumentId documentId, Document document, TextChange change, MethodEditPlan plan)
    {
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

    // "a" + x + "b" becomes $"a{x}b". Every operand must be a string literal or of type string; braces in literal text are doubled.
    private static async Task<MethodEditPlan> StringToInterpolationAsync(
        Solution solution, DocumentId documentId, Document document, SourceText text, SemanticModel model, SyntaxNode root, int line, MethodEditPlan plan)
    {
        var lineSpan = text.Lines[line - 1].Span;
        var chain = root.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Where(b => b.IsKind(SyntaxKind.AddExpression) && lineSpan.Contains(b.Span) && model.GetTypeInfo(b).Type?.SpecialType == SpecialType.System_String)
            .OrderByDescending(b => b.Span.Length)
            .FirstOrDefault();
        if (chain is null)
        {
            plan.Reasons.Add(Strings.Get("ConvertNotStringConcat"));
            return plan;
        }

        var operands = new List<ExpressionSyntax>();
        void Flatten(ExpressionSyntax e)
        {
            if (e is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } b && model.GetTypeInfo(b).Type?.SpecialType == SpecialType.System_String)
            {
                Flatten(b.Left);
                Flatten(b.Right);
            }
            else
            {
                operands.Add(e);
            }
        }

        Flatten(chain);
        var pieces = new System.Text.StringBuilder("$\"");
        foreach (var operand in operands)
        {
            if (operand is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal)
            {
                var content = literal.Token.ValueText.Replace("{", "{{").Replace("}", "}}");
                pieces.Append(content.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t"));
            }
            else if (model.GetTypeInfo(operand).Type?.SpecialType == SpecialType.System_String)
            {
                pieces.Append("{" + operand.WithoutTrivia() + "}");
            }
            else
            {
                plan.Reasons.Add(Strings.Get("ConvertInterpolationOperand"));
                return plan;
            }
        }

        pieces.Append('"');
        return await ApplyAndCheckAsync(solution, documentId, document, new TextChange(chain.Span, pieces.ToString()), plan).ConfigureAwait(false);
    }

    // A class becomes a record only where that is lossless: it declares no Equals, GetHashCode or ToString of its own, no operators, and
    // no base class other than object. Reference equality of instances would change, so a class that is compared by reference is not
    // checked here; the reply says so.
    private static async Task<MethodEditPlan> ClassToRecordAsync(
        Solution solution, DocumentId documentId, Document document, SourceText text, SemanticModel model, SyntaxNode? node, string keyword, MethodEditPlan plan)
    {
        var type = node?.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (type is null || model.GetDeclaredSymbol(type) is not INamedTypeSymbol symbol)
        {
            plan.Reasons.Add(Strings.Get("EditTypeNotAType"));
            return plan;
        }

        var overrides = type.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.ValueText is "Equals" or "GetHashCode" or "ToString")
            || type.Members.OfType<OperatorDeclarationSyntax>().Any();
        var baseNotObject = symbol.BaseType is not null && symbol.BaseType.SpecialType != SpecialType.System_Object;
        var finalizer = type.Members.OfType<DestructorDeclarationSyntax>().Any();
        var virtualMember = type.Members.Any(m => m.Modifiers.Any(x => x.IsKind(SyntaxKind.VirtualKeyword) || x.IsKind(SyntaxKind.AbstractKeyword) || x.IsKind(SyntaxKind.SealedKeyword)));
        if (overrides || baseNotObject || type.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) || (keyword == "struct" && (finalizer || virtualMember || type.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword) || m.IsKind(SyntaxKind.SealedKeyword)))))
        {
            plan.Reasons.Add(Strings.Get("ConvertNotLosslessRecord"));
            return plan;
        }

        var change = new TextChange(type.Keyword.Span, keyword);
        plan.Warnings.Add(keyword == "struct" ? Strings.Get("ConvertStructCheckCopies") : Strings.Get("ConvertRecordCheckEquality"));
        return await ApplyAndCheckAsync(solution, documentId, document, change, plan).ConfigureAwait(false);
    }

    // A property with only a getter and a method with no parameters: the uses are rewritten between Name and Name(). A property with a setter
    // is refused, since a method cannot assign it.
    private static async Task<MethodEditPlan> PropertyMethodAsync(
        Solution solution, DocumentId documentId, Document document, SourceText text, SemanticModel model, SyntaxNode? node, bool toMethod, MethodEditPlan plan)
    {
        var symbol = node?.AncestorsAndSelf().Select(n => model.GetDeclaredSymbol(n)).FirstOrDefault(s => s is IPropertySymbol or IMethodSymbol);
        if (toMethod && symbol is IPropertySymbol property)
        {
            if (property.SetMethod is not null || property.IsIndexer || property.GetMethod is null)
            {
                plan.Reasons.Add(Strings.Get("ConvertPropertyShape"));
                return plan;
            }

            var declaration = property.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<PropertyDeclarationSyntax>().FirstOrDefault();
            if (declaration is null)
            {
                plan.Reasons.Add(Strings.Get("ConvertPropertyShape"));
                return plan;
            }

            var getter = declaration.ExpressionBody?.Expression.ToString()
                ?? declaration.AccessorList?.Accessors.FirstOrDefault()?.Body?.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault()?.Expression?.ToString();
            if (getter is null)
            {
                plan.Reasons.Add(Strings.Get("ConvertPropertyShape"));
                return plan;
            }

            var access = declaration.Modifiers.ToString();
            var methodText = access + " " + property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + " Get" + property.Name + "() => " + getter + ";";
            var changes = new Dictionary<DocumentId, List<TextChange>> { [documentId] = new List<TextChange> { new TextChange(declaration.Span, methodText) } };
            var refs = await MethodEditEngine.CollectReferencesAsync(solution, property).ConfigureAwait(false);
            refs.RemoveAll(r => r.DocumentId == documentId && declaration.Span.Contains(r.Span));
            foreach (var reference in refs)
            {
                var refDocument = solution.GetDocument(reference.DocumentId);
                var refRoot = refDocument is null ? null : await refDocument.GetSyntaxRootAsync().ConfigureAwait(false);
                var token = refRoot?.FindToken(reference.Span.Start);
                if (token is null || token.Value.Text != property.Name)
                {
                    plan.Reasons.Add(Strings.Get("ConvertPropertyUseUnknown"));
                    return plan;
                }

                if (!changes.TryGetValue(reference.DocumentId, out var list))
                {
                    changes[reference.DocumentId] = list = new List<TextChange>();
                }

                list.Add(new TextChange(reference.Span, "Get" + property.Name + "()"));
            }

            var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
            await EditChecks.CheckNewDiagnosticsAsync(solution, edited, changes.Keys, plan).ConfigureAwait(false);
            plan.Files.AddRange(changes.Keys.Select(id => solution.GetDocument(id)!.FilePath ?? id.ToString()));
            plan.Warnings.Add(Strings.Format("ConvertPropertyToMethodDone", property.Name, refs.Count));
            if (!plan.Refused)
            {
                plan.Result = edited;
            }

            return plan;
        }

        return await MethodToPropertyAsync(solution, documentId, document, model, node, plan).ConfigureAwait(false);
    }

    // Every var local in the file whose type is nameable becomes that type, or every top-level string concatenation becomes an
    // interpolation. A site that cannot be converted is left as it is and counted in the reply, so one odd line does not block the rest.
    private static async Task<MethodEditPlan> FileWideAsync(
        Solution solution, DocumentId documentId, Document document, SourceText text, SemanticModel model, SyntaxNode root, string kind, MethodEditPlan plan)
    {
        var changes = new List<TextChange>();
        var skipped = 0;
        if (kind == "var-to-explicit-file")
        {
            foreach (var declaration in root.DescendantNodes().OfType<VariableDeclarationSyntax>().Where(d => d.Type.IsVar))
            {
                var initializer = declaration.Variables.FirstOrDefault()?.Initializer?.Value;
                var type = initializer is null ? null : model.GetTypeInfo(initializer).Type;
                if (type is null || type.IsAnonymousType || type.TypeKind == TypeKind.Error || declaration.Variables.Count != 1)
                {
                    skipped++;
                    continue;
                }

                changes.Add(new TextChange(declaration.Type.Span, type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            }
        }
        else
        {
            foreach (var chain in root.DescendantNodes().OfType<BinaryExpressionSyntax>()
                .Where(b => b.IsKind(SyntaxKind.AddExpression) && model.GetTypeInfo(b).Type?.SpecialType == SpecialType.System_String
                    && b.Parent is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression }))
            {
                var operands = new List<ExpressionSyntax>();
                void Flatten(ExpressionSyntax e)
                {
                    if (e is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } b && model.GetTypeInfo(b).Type?.SpecialType == SpecialType.System_String)
                    {
                        Flatten(b.Left);
                        Flatten(b.Right);
                    }
                    else
                    {
                        operands.Add(e);
                    }
                }

                Flatten(chain);
                var ok = true;
                var pieces = new System.Text.StringBuilder("$\"");
                foreach (var operand in operands)
                {
                    if (operand is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal)
                    {
                        pieces.Append(literal.Token.ValueText.Replace("{", "{{").Replace("}", "}}").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t"));
                    }
                    else if (model.GetTypeInfo(operand).Type?.SpecialType == SpecialType.System_String)
                    {
                        pieces.Append("{" + operand.WithoutTrivia() + "}");
                    }
                    else
                    {
                        ok = false;
                        break;
                    }
                }

                if (!ok)
                {
                    skipped++;
                    continue;
                }

                pieces.Append('"');
                changes.Add(new TextChange(chain.Span, pieces.ToString()));
            }
        }

        if (changes.Count == 0)
        {
            plan.Reasons.Add(Strings.Get("ConvertFileNothing"));
            return plan;
        }

        var edits = new Dictionary<DocumentId, List<TextChange>> { [documentId] = changes };
        var edited = await MethodEditEngine.ApplyAsync(solution, edits, plan.Divergent).ConfigureAwait(false);
        edited = await EditChecks.FormatFileAsync(edited, documentId, plan).ConfigureAwait(false);
        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, new[] { documentId }, plan).ConfigureAwait(false);
        plan.Files.Add(document.FilePath ?? "");
        plan.Warnings.Add(Strings.Format("ConvertFileDone", changes.Count, skipped));
        plan.Warnings.Add(Strings.Get("MethodEditWarnUnseen"));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    // "{ get; } = literal;" becomes "=> literal;": only a constant initializer, since an expression body re-evaluates on every read and
    // a constant does not change. "=> e;" becomes "{ get { return e; } }".
    private static async Task<MethodEditPlan> PropertyBodyAsync(
        Solution solution, DocumentId documentId, Document document, SourceText text, SemanticModel model, SyntaxNode? node, bool toExpression, MethodEditPlan plan)
    {
        var property = node?.AncestorsAndSelf().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
        if (property is null || model.GetDeclaredSymbol(property) is not IPropertySymbol)
        {
            plan.Reasons.Add(Strings.Get("ConvertNotAProperty"));
            return plan;
        }

        TextChange change;
        if (toExpression)
        {
            var accessors = property.AccessorList?.Accessors ?? default;
            var autoGet = accessors.Count == 1 && accessors[0].Keyword.IsKind(SyntaxKind.GetKeyword) && accessors[0].Body is null && accessors[0].ExpressionBody is null;
            if (!autoGet || property.Initializer?.Value is not LiteralExpressionSyntax literal || property.SemicolonToken.IsKind(SyntaxKind.None))
            {
                plan.Reasons.Add(Strings.Get("ConvertAutoShape"));
                return plan;
            }

            change = new TextChange(TextSpan.FromBounds(property.AccessorList!.SpanStart, property.SemicolonToken.Span.End), "=> " + literal.WithoutTrivia() + ";");
        }
        else
        {
            if (property.ExpressionBody is null || property.SemicolonToken.IsKind(SyntaxKind.None))
            {
                plan.Reasons.Add(Strings.Get("ConvertNotExpressionBodyProperty"));
                return plan;
            }

            change = new TextChange(TextSpan.FromBounds(property.ExpressionBody.SpanStart, property.SemicolonToken.Span.End),
                "{ get { return " + property.ExpressionBody.Expression.WithoutTrivia() + "; } }");
        }

        return await ApplyAndCheckAsync(solution, documentId, document, change, plan).ConfigureAwait(false);
    }

    // A method with no parameters, one return of an expression, and a non-void return becomes "Type Name => expr;"; calls "Name()" become "Name".
    private static async Task<MethodEditPlan> MethodToPropertyAsync(
        Solution solution, DocumentId documentId, Document document, SemanticModel model, SyntaxNode? node, MethodEditPlan plan)
    {
        var declaration = node?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (declaration is null || model.GetDeclaredSymbol(declaration) is not IMethodSymbol symbol)
        {
            plan.Reasons.Add(Strings.Get("ConvertNotAMethod"));
            return plan;
        }

        var expression = declaration.ExpressionBody?.Expression
            ?? (declaration.Body is { Statements.Count: 1 } body && body.Statements[0] is ReturnStatementSyntax { Expression: { } value } ? value : null);
        if (expression is null || symbol.Parameters.Length > 0 || symbol.ReturnsVoid || symbol.IsAsync || symbol.IsGenericMethod || symbol.IsOverride || symbol.IsVirtual
            || symbol.ContainingType.TypeKind == TypeKind.Interface || symbol.ExplicitInterfaceImplementations.Length > 0)
        {
            plan.Reasons.Add(Strings.Get("ConvertMethodShape"));
            return plan;
        }

        var access = declaration.Modifiers.ToString();
        var propertyText = (access.Length > 0 ? access + " " : "") + symbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            + " " + symbol.Name + " => " + expression.WithoutTrivia() + ";";
        var changes = new Dictionary<DocumentId, List<TextChange>> { [documentId] = new List<TextChange> { new TextChange(declaration.Span, propertyText) } };
        var references = await MethodEditEngine.CollectReferencesAsync(solution, symbol).ConfigureAwait(false);
        references.RemoveAll(r => r.DocumentId == documentId && declaration.Span.Contains(r.Span));
        foreach (var reference in references)
        {
            var refDocument = solution.GetDocument(reference.DocumentId);
            var refRoot = refDocument is null ? null : await refDocument.GetSyntaxRootAsync().ConfigureAwait(false);
            var call = refRoot is null ? null : CallSiteRewriter.CallOf(refRoot.FindToken(reference.Span.Start)) as InvocationExpressionSyntax;
            if (call is null || call.ArgumentList.Arguments.Count > 0)
            {
                plan.Reasons.Add(Strings.Get("ConvertMethodUseNotCall"));
                return plan;
            }

            if (!changes.TryGetValue(reference.DocumentId, out var list))
            {
                changes[reference.DocumentId] = list = new List<TextChange>();
            }

            list.Add(new TextChange(call.Span, symbol.Name));
        }

        var edited = await MethodEditEngine.ApplyAsync(solution, changes, plan.Divergent).ConfigureAwait(false);
        edited = await EditChecks.FormatFileAsync(edited, documentId, plan).ConfigureAwait(false);
        await EditChecks.CheckNewDiagnosticsAsync(solution, edited, changes.Keys, plan).ConfigureAwait(false);
        plan.Files.AddRange(changes.Keys.Select(id => solution.GetDocument(id)!.FilePath ?? id.ToString()));
        plan.Warnings.Add(Strings.Format("ConvertMethodToPropertyDone", symbol.Name, references.Count));
        if (!plan.Refused)
        {
            plan.Result = edited;
        }

        return plan;
    }

    private static bool IsValueType(TypeSyntax type) => type is PredefinedTypeSyntax predefined && !predefined.Keyword.IsKind(SyntaxKind.StringKeyword)
        && !predefined.Keyword.IsKind(SyntaxKind.ObjectKeyword);
}
